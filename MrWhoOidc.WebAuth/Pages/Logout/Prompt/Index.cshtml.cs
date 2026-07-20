using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using Microsoft.AspNetCore.WebUtilities;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Logout.Prompt;

public class IndexModel : PageModel
{
    public string ProviderDisplay { get; set; } = "Provider";
    public string ProviderIconClass { get; set; } = "ph ph-sign-out"; // default icon
    public string ReturnUrl { get; set; } = "/";
    public string? Style { get; set; }
    public string? ClientId { get; set; }
    public string? PostLogoutRedirectUri { get; set; }

    private readonly IUpstreamLogoutService _upstream;
    private readonly AuthDbContext _db;
    private readonly IKeyStore _keyStore; // currently not required directly but left for future UI decisions
    private readonly IOidcMetrics _metrics;
    private readonly IAuditSink _audit;
    private readonly ILogger<IndexModel> _logger;
    private readonly FederatedLogoutOptions _fedOpts;
    private readonly ITenantSupportAccessService _supportAccessService;

    public IndexModel(IUpstreamLogoutService upstream, AuthDbContext db, IKeyStore keyStore, IOidcMetrics metrics, IAuditSink audit, ILogger<IndexModel> logger, IOptions<FederatedLogoutOptions> fedOpts, ITenantSupportAccessService supportAccessService)
    {
        _upstream = upstream; _db = db; _keyStore = keyStore; _metrics = metrics; _audit = audit; _logger = logger; _fedOpts = fedOpts.Value; _supportAccessService = supportAccessService;
    }

    public void OnGet(string? provider, string? ret, string? style, string? client_id, string? post_logout_redirect_uri)
    {
        // Ensure logout prompt is not cached by browsers or intermediate proxies
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        if (!string.IsNullOrEmpty(provider))
        {
            ProviderDisplay = NormalizeProviderName(provider);
            ProviderIconClass = ResolveIcon(provider);
        }
        if (!string.IsNullOrEmpty(ret) && Url.IsLocalUrl(ret)) ReturnUrl = ret;
        if (!string.IsNullOrWhiteSpace(style)) Style = style;
        if (!string.IsNullOrWhiteSpace(client_id)) ClientId = client_id;
        if (!string.IsNullOrWhiteSpace(post_logout_redirect_uri)) PostLogoutRedirectUri = post_logout_redirect_uri;
    }

    public async Task<IActionResult> OnPostAsync(string mode, string returnUrl, string? style, string? client_id, string? post_logout_redirect_uri)
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrEmpty(mode)) mode = "local";
        if (!string.IsNullOrEmpty(style)) Style = style;
        if (string.IsNullOrEmpty(returnUrl) || !Url.IsLocalUrl(returnUrl)) returnUrl = "/";

        // Capture potential external redirect inputs (optional) - now from form fields
        var clientId = client_id ?? string.Empty;
        var externalPostLogout = post_logout_redirect_uri ?? string.Empty;

        if (mode == "local")
        {
            _metrics.LogoutLocal.Add(1);
            _audit.Emit("logout.federated.choice.local", new { return_hash = _audit.HashValue(returnUrl) });
            await _supportAccessService.StopSupportAccessAsync(HttpContext).ConfigureAwait(false);
            await HttpContext.SignOutAsync();
            _metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "local"));
            return LocalRedirect(returnUrl);
        }
        if (mode == "federated")
        {
            _audit.Emit("logout.federated.choice.federated", new { return_hash = _audit.HashValue(returnUrl) });
            var capability = await _upstream.CanFederateAsync(User, HttpContext.RequestAborted);
            if (!capability.CanFederate)
            {
                _logger.LogWarning("Federated logout chosen but capability missing - falling back to local");
                _metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", "capability_missing"));
                _audit.Emit("logout.federated.choice.federated.capability_missing", new { });
                await _supportAccessService.StopSupportAccessAsync(HttpContext).ConfigureAwait(false);
                await HttpContext.SignOutAsync();
                _metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "fallback_local"));
                return LocalRedirect(returnUrl);
            }

            string? encIdToken = null; string? upstreamSid = null;
            if (User?.Identity?.IsAuthenticated == true)
            {
                var authResult = await HttpContext.AuthenticateAsync();
                encIdToken = authResult?.Properties?.Items?.TryGetValue("UpstreamIdTokenEnc", out var enc) == true ? enc : null;
                upstreamSid = authResult?.Properties?.Items?.TryGetValue("UpstreamSid", out var sidVal) == true ? sidVal : null;
            }
            var callbackBase = HttpContext.GetIssuer();
            var redirectModel = await _upstream.BuildFederatedRedirectAsync(User ?? new ClaimsPrincipal(), encIdToken, upstreamSid, callbackBase, returnUrl, clientId, externalPostLogout, HttpContext.RequestAborted);
            if (!redirectModel.Success)
            {
                _logger.LogWarning("Failed to build federated logout redirect: {Reason}", redirectModel.FailureReason);
                _metrics.LogoutFailures.Add(1, new KeyValuePair<string, object?>("reason", redirectModel.FailureReason));
                _audit.Emit("logout.federated.redirect.fail", new { reason = redirectModel.FailureReason });
                await _supportAccessService.StopSupportAccessAsync(HttpContext).ConfigureAwait(false);
                await HttpContext.SignOutAsync();
                _metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "fallback_local"));
                return LocalRedirect(returnUrl);
            }

            await HttpContext.SignOutAsync();
            _metrics.LogoutFederated.Add(1);
            _metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "federated_redirect"));
            return Redirect(redirectModel.RedirectUrl ?? "/");
        }

        _audit.Emit("logout.federated.choice.unknown", new { mode });
        await _supportAccessService.StopSupportAccessAsync(HttpContext).ConfigureAwait(false);
        await HttpContext.SignOutAsync();
        _metrics.LogoutDuration.Record(sw.ElapsedMilliseconds, new KeyValuePair<string, object?>("mode", "unknown_local"));
        return LocalRedirect(returnUrl);
    }

    private static string NormalizeProviderName(string raw)
    {
        return raw.Trim() switch
        {
            "google" or "Google" => "Google",
            "auth0" or "Auth0" => "Auth0",
            "microsoft" or "AzureAD" or "azuread" => "Microsoft",
            _ => raw.Trim()
        };
    }

    private static string ResolveIcon(string raw)
    {
        return raw.ToLowerInvariant() switch
        {
            "google" => "ph ph-google-logo",
            "auth0" => "ph ph-shield-check",
            "microsoft" or "azuread" => "ph ph-windows-logo",
            _ => "ph ph-sign-out"
        };
    }
}
