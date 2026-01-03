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
    ITenantAccessor tenantAccessor
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

            var validationResult = await validator.ValidateAsync(domainReq, http.RequestAborted);
            if (!validationResult.IsValid)
            {
                outcome = "error";
                logger.LogWarning("/authorize 400: validation failed corr={Corr} client={Client} error={Error}", corr, clientBucket, validationResult.Error);
                return responseGenerator.CreateErrorResponse(http, validationResult, corr);
            }

            var isAuthenticated = http.User.Identity?.IsAuthenticated ?? false;

            var promptValues = validationResult.PromptValues
                ?? http.Request.Query["prompt"].ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => p.Trim().ToLowerInvariant())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

            var hasPromptNone = promptValues.Contains("none", StringComparer.Ordinal);
            var hasPromptLogin = promptValues.Contains("login", StringComparer.Ordinal);
            var hasPromptConsent = promptValues.Contains("consent", StringComparer.Ordinal);
            var hasPromptSelectAccount = promptValues.Contains("select_account", StringComparer.Ordinal);

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

            var lastIdpName = ".mrwhooidc.lastidp." + Bucketization.BucketizeClientId(validationResult.ClientId!);
            http.Request.Cookies.TryGetValue(lastIdpName, out var lastUsedIdp);

            var forceAccountSelection = hasPromptSelectAccount;

            var selectionResult = await providerSelection.EvaluateAsync(
                validationResult.ClientId!,
                http.Request.Query["idp"].ToString(),
                http.Request.Query["idp_hint"].ToString(),
                lastUsedIdp,
                forceAccountSelection,
                http.RequestAborted);

            if (selectionResult.AllowQr && http.Request.Query.ContainsKey("qr"))
            {
                outcome = "qr_initiate";
                return await qrLoginHandler.InitiateAsync(http, validationResult, domainReq);
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
                return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
            }

            // If the RP requested re-authentication or account selection, force an auth redirect.
            if (hasPromptLogin || hasPromptSelectAccount)
            {
                outcome = hasPromptLogin ? "prompt_login" : "prompt_select_account";
                return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
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
                    return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
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
                    return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
                }
            }

            // ACR enforcement (best-effort): validate requested ACR values and require interaction if current session doesn't match.
            if (validationResult.AcrValues is { Length: > 0 } requestedAcr)
            {
                var supported = authOptions.Value.AcrValuesSupported;
                if (supported is { Length: > 0 })
                {
                    var unsupported = requestedAcr.Where(v => !supported.Contains(v, StringComparer.Ordinal)).ToArray();
                    if (unsupported.Length > 0)
                    {
                        outcome = "acr_values_not_supported";
                        return responseGenerator.CreateErrorResponse(
                            http,
                            validationResult with
                            {
                                Error = "acr_values_not_supported",
                                ErrorDescription = $"Unsupported acr_values requested: {string.Join(", ", unsupported)}"
                            },
                            corr);
                    }
                }

                var currentAcr = http.User.FindFirst(OidcConstants.Claims.Acr)?.Value;
                if (string.IsNullOrWhiteSpace(currentAcr) || !requestedAcr.Contains(currentAcr, StringComparer.Ordinal))
                {
                    if (hasPromptNone)
                    {
                        outcome = "prompt_none_acr";
                        return responseGenerator.CreateErrorResponse(
                            http,
                            validationResult with
                            {
                                Error = "interaction_required",
                                ErrorDescription = "Silent authentication requested but the requested ACR cannot be satisfied by the current session"
                            },
                            corr);
                    }

                    outcome = "acr";
                    return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
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
                return responseGenerator.CreateErrorResponse(http, validationResult with { Error = "access_denied", ErrorDescription = assignmentError }, corr);
            }

            // prompt=consent: force consent UX even if user already granted.
            if (hasPromptConsent)
            {
                outcome = "prompt_consent";
                return responseGenerator.CreateConsentRedirect(http, validationResult, BuildTenantAwareUrl("/consent"));
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
                return responseGenerator.CreateConsentRedirect(http, validationResult, BuildTenantAwareUrl("/consent"));
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
                    var (ok, _, r, c) = await codes.IssueAsync(validationResult, userId);
                    if (!ok || r is null)
                    {
                        errorResult = ErrorResults.ServerError($"Failed to issue authorization code (corr={corr})");
                        return;
                    }
                    code = c;
                    redirect = r;

                    await metadataService.PopulateMetadataAsync(http, code!, http.RequestAborted);

                    if (context.Mode == "par")
                    {
                        parStore.MarkConsumedById(context.RequestUriRaw!);
                    }

                    await transaction.CommitAsync(http.RequestAborted);
                }
            });

            if (errorResult != null) return errorResult;

            outcome = "success";
            return responseGenerator.CreateSuccessResponse(http, validationResult, code!, redirect);
        }
        catch (Exception ex)
        {
            outcome = "error";
            logger.LogError(ex, "Unhandled error in /authorize");
            return ErrorResults.ServerError("An internal error occurred");
        }
        finally
        {
            sw.Stop();
            metrics.AuthorizeDurationMs.Record(sw.Elapsed.TotalMilliseconds, new TagList { new("outcome", outcome) });
        }
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
