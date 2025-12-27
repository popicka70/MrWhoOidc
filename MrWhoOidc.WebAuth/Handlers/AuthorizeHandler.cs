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

            var lastIdpName = ".mrwhooidc.lastidp." + Bucketization.BucketizeClientId(validationResult.ClientId!);
            http.Request.Cookies.TryGetValue(lastIdpName, out var lastUsedIdp);

            var prompt = http.Request.Query["prompt"].ToString();
            var forceAccountSelection = !string.IsNullOrEmpty(prompt) && prompt.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(p => string.Equals(p, "select_account", StringComparison.OrdinalIgnoreCase));

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

            if (!http.User.Identity?.IsAuthenticated ?? true)
            {
                return await authRedirect.RedirectToLoginAsync(http, selectionResult, validationResult, http.RequestAborted);
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

            var consentDecision = await consentProcessor.EvaluateAsync(userId, validationResult.ClientId!, validationResult.Scopes ?? Array.Empty<string>(), http.RequestAborted);
            if (consentDecision.RequiresConsent && !consentDecision.HasConsent)
            {
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
}
