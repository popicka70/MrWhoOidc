using MrWhoOidc.WebAuth.Observability;
using System.Diagnostics;
using System.Security.Claims;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Utils;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Protocols;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IAuthorizeHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class AuthorizeHandler(
    IAuthorizeRequestValidator validator,
    IAuditSink audit,
    IConsentProcessor consentProcessor,
    IProviderSelectionService providerSelection,
    IUserClientAssignmentService userAssignments,
    IAuthorizeResponseGenerator responseGenerator,
    IAuthorizeRequestSanitizer sanitizer,
    IAuthenticationRedirectService authRedirect,
    IAuthorizationMetadataService metadataService,
    IAuthorizeRequestOrchestrator orchestrator,
    IAuthorizationCodeService codes,
    OidcEndpointMetrics metrics,
    IPushedAuthorizationRequestStore parStore,
    IOptions<AuthOptions> authOptions,
    ILogger<AuthorizeHandler> logger,
    AuthDbContext db,
    IQrLoginHandler qrLoginHandler,
    ITenantAccessor tenantAccessor,
    IAuthorizeInteractionStore? interactionStore = null
) : IAuthorizeHandler
{
    public async Task<IResult> HandleAsync(HttpContext http)
    {
        logger.LogInformation("⚡ /authorize called Path={Path}", http.Request.Path);
        var sw = Stopwatch.StartNew();
        string outcome = "redirect";
        try
        {
            var sanitizeResult = sanitizer.SanitizeAddressBar(http);
            if (sanitizeResult != null) return sanitizeResult;

            var (error, context) = await orchestrator.ResolveAndValidateAsync(http, http.RequestAborted);
            if (error != null)
            {
                outcome = "error";
                return error;
            }

            var domainReq = context!.Request;
            var corr = context.CorrelationId;
            var clientBucket = context.ClientBucket;
            var requestParameters = await AuthorizeReturnUrlHelper.GetRequestParametersAsync(http).ConfigureAwait(false);

            var validationResult = await validator.ValidateAsync(domainReq, http.RequestAborted);
            if (!validationResult.IsValid)
            {
                outcome = "error";
                audit.Emit("authorize.request.rejected", new
                {
                    client_id = validationResult.ClientId,
                    error = validationResult.Error,
                    corr
                });
                logger.LogWarning("/authorize 400: validation failed corr={Corr} client={Client} error={Error}", corr, clientBucket, validationResult.Error);
                return responseGenerator.CreateErrorResponse(http, validationResult, corr);
            }

            var isAuthenticated = http.User.Identity?.IsAuthenticated ?? false;

            // RFC 9101 §6.3 / RFC 9126 §4: with a request object or PAR only the request object's parameters count,
            // so the unsigned front-channel prompt must not be consulted. (In query mode PromptValues already comes
            // from the query, so the fallback only covers the same values.)
            var usesRequestObject = context.Mode is "jar" or "par";
            var promptValues = validationResult.PromptValues
                ?? (usesRequestObject
                    ? Array.Empty<string>()
                    : (AuthorizeReturnUrlHelper.GetParameterValue(requestParameters, OidcConstants.Parameters.Prompt) ?? string.Empty)
                        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());

            var hasPromptNone = promptValues.Contains("none", StringComparer.Ordinal);
            var hasPromptLogin = promptValues.Contains("login", StringComparer.Ordinal);
            var hasPromptConsent = promptValues.Contains("consent", StringComparer.Ordinal);
            var hasPromptSelectAccount = promptValues.Contains("select_account", StringComparer.Ordinal);

            // A JAR/PAR request cannot have prompt stripped from its (signed / pushed) parameters after the user
            // completed the interaction, so the resumed request is matched against the server-side marker instead:
            // login/select_account count as satisfied once the session authenticated after the interaction started,
            // consent once the user completed the consent screen for this request.
            var resumed = context.ResumedInteraction;
            if (resumed is not null && isAuthenticated)
            {
                if ((hasPromptLogin || hasPromptSelectAccount)
                    && TryGetAuthTime(http.User, out var resumedAuthTime)
                    && resumedAuthTime.ToUnixTimeSeconds() >= resumed.StartedAt)
                {
                    hasPromptLogin = false;
                    hasPromptSelectAccount = false;
                }

                if (hasPromptConsent && resumed.ConsentGivenAt is not null)
                {
                    hasPromptConsent = false;
                }
            }

            // OIDC prompt=none: fail fast if we would need to interact.
            if (hasPromptNone && !isAuthenticated)
            {
                outcome = "prompt_none_no_session";
                return responseGenerator.CreateErrorResponse(
                    http,
                    validationResult with
                    {
                        Error = "login_required",
                        ErrorDescription = "Silent authentication requested but no active session is present"
                    },
                    corr);
            }

            var lastIdpName = "__Host-mrwhooidc-lastidp-" + Bucketization.BucketizeClientId(validationResult.ClientId!);
            http.Request.Cookies.TryGetValue(lastIdpName, out var lastUsedIdp);

            var forceAccountSelection = hasPromptSelectAccount;
            var providerTenantId = tenantAccessor.CurrentTenant?.IsMultiTenantMode == true
                ? tenantAccessor.CurrentTenant.TenantId
                : (Guid?)null;

            var selectionResult = await providerSelection.EvaluateAsync(
                validationResult.ClientId!,
                AuthorizeReturnUrlHelper.GetParameterValue(requestParameters, "idp") ?? string.Empty,
                AuthorizeReturnUrlHelper.GetParameterValue(requestParameters, "idp_hint") ?? string.Empty,
                lastUsedIdp,
                forceAccountSelection,
                http.RequestAborted,
                providerTenantId);

            if (selectionResult.AllowQr && requestParameters.Any(static pair => string.Equals(pair.Key, "qr", StringComparison.Ordinal)))
            {
                outcome = "qr_initiate";
                return await BeginInteractionAsync(http, context, qrLoginHandler.InitiateAsync(http, validationResult, domainReq));
            }

            // prompt=none cannot show provider selection UI.
            if (hasPromptNone && selectionResult.RequiresSelection)
            {
                outcome = "prompt_none_account_selection";
                return responseGenerator.CreateErrorResponse(
                    http,
                    validationResult with
                    {
                        Error = "account_selection_required",
                        ErrorDescription = "Silent authentication requested but account selection is required"
                    },
                    corr);
            }

            if (!isAuthenticated)
            {
                audit.Emit("authorize.interaction.login_required", new
                {
                    client_id = validationResult.ClientId,
                    reason = "not_authenticated",
                    corr
                });
                return await BeginInteractionAsync(http, context, authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, domainReq.display, http.RequestAborted));
            }

            // If the RP requested re-authentication or account selection, force an auth redirect.
            if (hasPromptLogin || hasPromptSelectAccount)
            {
                outcome = hasPromptLogin ? "prompt_login" : "prompt_select_account";
                audit.Emit("authorize.interaction.login_required", new
                {
                    client_id = validationResult.ClientId,
                    reason = outcome,
                    corr
                });
                return await BeginInteractionAsync(http, context, authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, domainReq.display, http.RequestAborted));
            }

            // max_age enforcement (OIDC): if we can't prove freshness, require re-auth.
            if (validationResult.MaxAgeSeconds is not null)
            {
                if (!TryGetAuthTime(http.User, out var authTimeUtc))
                {
                    if (hasPromptNone)
                    {
                        outcome = "prompt_none_max_age_missing_auth_time";
                        return responseGenerator.CreateErrorResponse(
                            http,
                            validationResult with
                            {
                                Error = "login_required",
                                ErrorDescription = "Silent authentication requested but auth_time is not available"
                            },
                            corr);
                    }

                    outcome = "max_age_missing_auth_time";
                    return await BeginInteractionAsync(http, context, authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, domainReq.display, http.RequestAborted));
                }

                var ageSeconds = (int)Math.Floor((DateTimeOffset.UtcNow - authTimeUtc).TotalSeconds);
                if (ageSeconds > validationResult.MaxAgeSeconds.Value)
                {
                    if (hasPromptNone)
                    {
                        outcome = "prompt_none_max_age";
                        return responseGenerator.CreateErrorResponse(
                            http,
                            validationResult with
                            {
                                Error = "login_required",
                                ErrorDescription = "Silent authentication requested but max_age requires re-authentication"
                            },
                            corr);
                    }

                    outcome = "max_age";
                    return await BeginInteractionAsync(http, context, authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, domainReq.display, http.RequestAborted));
                }
            }

            // Only enforce ACR when the OP advertises supported values. If discovery omits
            // acr_values_supported, tolerate arbitrary requested values and continue.
            if (validationResult.AcrValues is { Length: > 0 } requestedAcr)
            {
                var supported = authOptions.Value.AcrValuesSupported;
                // acr_values is a voluntary request (OIDC Core §3.1.2.1): values this OP does not support are
                // ignored rather than rejected. Step up only when at least one requested value is achievable.
                if (supported is { Length: > 0 } && requestedAcr.Any(v => supported.Contains(v, StringComparer.Ordinal)))
                {
                    var currentAcr = http.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
                    if (string.IsNullOrWhiteSpace(currentAcr) || !requestedAcr.Contains(currentAcr, StringComparer.Ordinal))
                    {
                        if (hasPromptNone)
                        {
                            outcome = "prompt_none_acr";
                            // OIDC Core §3.1.2.6: the step-up needs the user to authenticate again. RFC 9470's
                            // insufficient_user_authentication is a resource-server error, not an /authorize one.
                            return responseGenerator.CreateErrorResponse(
                                http,
                                validationResult with
                                {
                                    Error = "login_required",
                                    ErrorDescription = "Silent authentication requested but the requested ACR cannot be satisfied by the current session"
                                },
                                corr);
                        }

                        outcome = "acr";
                        return await BeginInteractionAsync(http, context, authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, domainReq.display, http.RequestAborted));
                    }
                }
            }

            var sub = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
            {
                logger.LogWarning("❌ No valid sub claim found, returning Unauthorized");
                return Results.Unauthorized();
            }

            var idp = http.User.FindFirst("idp")?.Value;
            var (assigned, assignmentError) = await userAssignments.EnsureAssignedAsync(userId, validationResult.ClientId!, idp, http.RequestAborted);
            if (!assigned)
            {
                outcome = "not_assigned";
                audit.Emit("authorize.access_denied", new
                {
                    client_id = validationResult.ClientId,
                    reason = "user_not_assigned",
                    corr
                });
                return responseGenerator.CreateErrorResponse(http, validationResult with { Error = "access_denied", ErrorDescription = assignmentError }, corr);
            }

            // prompt=consent: force consent UX even if user already granted.
            if (hasPromptConsent)
            {
                outcome = "prompt_consent";
                audit.Emit("authorize.interaction.consent_required", new
                {
                    client_id = validationResult.ClientId,
                    reason = "prompt_consent",
                    corr
                });
                return await BeginInteractionAsync(http, context, Task.FromResult(responseGenerator.CreateConsentRedirect(http, validationResult, BuildTenantAwareUrl("/consent"))));
            }

            var consentDecision = await consentProcessor.EvaluateAsync(userId, validationResult.ClientId!, validationResult.Scopes ?? Array.Empty<string>(), http.RequestAborted);
            if (consentDecision.RequiresConsent && !consentDecision.HasConsent)
            {
                if (hasPromptNone)
                {
                    outcome = "prompt_none_consent";
                    return responseGenerator.CreateErrorResponse(
                        http,
                        validationResult with
                        {
                            Error = "consent_required",
                            ErrorDescription = "Silent authentication requested but user consent is required"
                        },
                        corr);
                }

                outcome = "consent";
                audit.Emit("authorize.interaction.consent_required", new
                {
                    client_id = validationResult.ClientId,
                    reason = "missing_consent",
                    corr
                });
                return await BeginInteractionAsync(http, context, Task.FromResult(responseGenerator.CreateConsentRedirect(http, validationResult, BuildTenantAwareUrl("/consent"))));
            }

            logger.LogInformation("🔐 Proceeding to issue authorization code for client {ClientId}", validationResult.ClientId);

            string? code = null;
            string? redirect = null;
            IResult? errorResult = null;

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                using (var transaction = await db.Database.BeginTransactionAsync(http.RequestAborted))
                {
                    TryGetAuthTime(http.User, out var authTimeUtc);
                    var (ok, _, r, c) = await codes.IssueAsync(validationResult, userId, authTime: authTimeUtc != default ? authTimeUtc : (DateTimeOffset?)null);
                    if (!ok || r is null)
                    {
                        errorResult = ErrorResults.ServerError($"Failed to issue authorization code (corr={corr})");
                        return;
                    }
                    code = c;
                    redirect = r;

                    await metadataService.PopulateMetadataAsync(http, code!, http.RequestAborted);

                    // RFC 9126 §4: a request_uri is single-use. Consuming inside the code-issuance transaction
                    // means a replayed (or concurrently redeemed) request_uri rolls back its code.
                    if (context.Mode == "par" && (context.ParId is null || !parStore.MarkConsumedById(context.ParId)))
                    {
                        logger.LogWarning("/authorize PAR request_uri already used or expired corr={Corr}", corr);
                        errorResult = AuthorizeLocalErrorResults.Create(http, OAuthConstants.ErrorCodes.InvalidRequest, "Invalid or expired request_uri", corr);
                        return; // transaction disposed without commit -> code row rolled back
                    }

                    await transaction.CommitAsync(http.RequestAborted);
                }
            });

            if (errorResult != null) return errorResult;

            // Authorization finished: any later use of the same request object / request_uri is a new use.
            if (context.InteractionKey is not null && interactionStore is not null)
            {
                await interactionStore.CompleteAsync(http, context.InteractionKey, http.RequestAborted);
            }

            outcome = "success";
            audit.Emit("authorize.code.issued", new
            {
                client_id = validationResult.ClientId,
                corr,
                has_state = !string.IsNullOrWhiteSpace(validationResult.State)
            });
            return responseGenerator.CreateSuccessResponse(http, validationResult, code!, redirect);
        }
        catch (Exception ex)
        {
            outcome = "error";
            audit.Emit("authorize.error", new
            {
                path = http.Request.Path.ToString(),
                error = ex.GetType().Name
            });
            logger.LogError(ex, "Unhandled error in /authorize");
            return ErrorResults.ServerError("An internal error occurred");
        }
        finally
        {
            sw.Stop();
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("outcome", outcome) });
        }
    }

    private async Task<IResult> BeginInteractionAsync(HttpContext http, AuthorizationContext context, Task<IResult> interaction)
    {
        var result = await interaction.ConfigureAwait(false);
        if (context.InteractionKey is not null && interactionStore is not null)
        {
            await interactionStore.BeginAsync(http, context.InteractionKey, http.RequestAborted).ConfigureAwait(false);
        }

        return result;
    }

    private string BuildTenantAwareUrl(string path)
    {
        var currentTenant = tenantAccessor.CurrentTenant;
        if (!path.StartsWith('/')) path = "/" + path;
        if (currentTenant != null && currentTenant.IsMultiTenantMode) return $"/t/{currentTenant.Slug}{path}";
        return path;
    }

    private static bool TryGetAuthTime(ClaimsPrincipal user, out DateTimeOffset authTimeUtc)
    {
        authTimeUtc = default;
        var raw = user.FindFirst(OidcConstants.Claims.AuthTime)?.Value;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        if (!long.TryParse(raw, out var seconds)) return false;
        if (seconds <= 0) return false;
        authTimeUtc = DateTimeOffset.FromUnixTimeSeconds(seconds);
        return true;
    }
}
