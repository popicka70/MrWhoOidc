using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.IdentityProviders;
using System.Net.Http;
using System;
using System.Threading;
using System.Threading.Tasks;
using MrWhoOidc.WebAuth.Observability;

namespace MrWhoOidc.WebAuth.Handlers;

public sealed class FederatedLogoutOptions
{
    public bool Enabled { get; set; } = true;
    public int StateTtlSeconds { get; set; } = 300; // 5 min
}

public record FederatedCapability(bool CanFederate, string? ProviderName, string? ProviderDisplayName);
public record FederatedRedirectResult(bool Success, string? RedirectUrl, string? FailureReason);
public record FederatedCallbackValidation(bool Valid, string? Reason);

public interface IUpstreamLogoutService
{
    Task<FederatedCapability> CanFederateAsync(ClaimsPrincipal principal, CancellationToken ct);
    /// <summary>
    /// Build redirect to upstream end_session_endpoint. upstreamIdTokenEnc is an encrypted upstream id_token captured at sign-in; may be null.
    /// upstreamSid optional session identifier extracted from upstream id_token. callbackBase should be scheme://host of current request.
    /// </summary>
    Task<FederatedRedirectResult> BuildFederatedRedirectAsync(ClaimsPrincipal principal, string? upstreamIdTokenEnc, string? upstreamSid, string callbackBase, string? returnUrl, CancellationToken ct);
    Task<FederatedCallbackValidation> ValidateCallbackAsync(string? state, CancellationToken ct);
}

internal sealed class UpstreamLogoutService : IUpstreamLogoutService
{
    private readonly IMemoryCache _cache;
    private readonly IOptions<FederatedLogoutOptions> _opts;
    private readonly ILogger<UpstreamLogoutService> _logger;
    private readonly IDataProtector _stateProtector;
    private readonly IDataProtector _idTokenProtector;
    private readonly AuthDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IAuditSink _audit;

    private const string CachePrefix = "fedlogout_state_";
    private const string DiscoCachePrefix = "fedlogout_disco_";

    public UpstreamLogoutService(IMemoryCache cache,
        IOptions<FederatedLogoutOptions> opts,
        IDataProtectionProvider dp,
        ILogger<UpstreamLogoutService> logger,
        AuthDbContext db,
        IHttpClientFactory http,
        IAuditSink audit)
    {
        _cache = cache; _opts = opts; _logger = logger; _db = db; _http = http; _audit = audit;
        _stateProtector = dp.CreateProtector("federated-logout-state");
        _idTokenProtector = dp.CreateProtector("federated-logout-idtoken");
    }

    public async Task<FederatedCapability> CanFederateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!_opts.Value.Enabled) return new FederatedCapability(false, null, null);
        var idpName = principal?.Claims.FirstOrDefault(c => c.Type == "idp")?.Value;
        if (string.IsNullOrEmpty(idpName)) return new FederatedCapability(false, null, null);

        // Quick existence check in DB; avoid full discovery until needed.
        var provider = await _db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == idpName && p.Enabled, ct);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson)) return new FederatedCapability(false, null, null);

        // Parse config to get Authority (needed for discovery later)
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null) return new FederatedCapability(false, null, null);

        return new FederatedCapability(true, provider.Name, provider.DisplayName ?? provider.Name);
    }

    public async Task<FederatedRedirectResult> BuildFederatedRedirectAsync(ClaimsPrincipal principal, string? upstreamIdTokenEnc, string? upstreamSid, string callbackBase, string? returnUrl, CancellationToken ct)
    {
    var idpName = principal?.Claims.FirstOrDefault(c => c.Type == "idp")?.Value;
    if (string.IsNullOrEmpty(idpName)) return new FederatedRedirectResult(false, null, "missing_idp");
    var startTs = DateTimeOffset.UtcNow;

        var provider = await _db.IdentityProviders.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Name == idpName && p.Enabled, ct);
        if (provider is null || string.IsNullOrWhiteSpace(provider.ConfigJson)) return new FederatedRedirectResult(false, null, "provider_not_found");
        if (!OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok || cfg is null) return new FederatedRedirectResult(false, null, "invalid_config");

        // Get discovery document (cache short-lived)
        var discoKey = DiscoCachePrefix + provider.Id.ToString("N");
        JsonDocument? discoDoc = null;
        if (!_cache.TryGetValue(discoKey, out string? discoJson))
        {
            try
            {
                var httpc = _http.CreateClient();
                var discoUrl = string.IsNullOrWhiteSpace(cfg.DiscoveryUrl) ? cfg.Authority.TrimEnd('/') + "/.well-known/openid-configuration" : cfg.DiscoveryUrl!;
                using var resp = await httpc.GetAsync(discoUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Federated logout discovery failed {Status} for {Url}", (int)resp.StatusCode, discoUrl);
                    _audit.Emit("logout.federated.discovery.fail", new { provider = idpName, status = (int)resp.StatusCode, url = discoUrl });
                    return new FederatedRedirectResult(false, null, "discovery_failed");
                }
                discoJson = await resp.Content.ReadAsStringAsync(ct);
                _cache.Set(discoKey, discoJson, TimeSpan.FromMinutes(10));
                _audit.Emit("logout.federated.discovery.ok", new { provider = idpName });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Federated logout discovery exception for provider {Provider}", provider.Name);
                _audit.Emit("logout.federated.discovery.exception", new { provider = idpName, error = ex.GetType().Name });
                return new FederatedRedirectResult(false, null, "discovery_exception");
            }
        }
        try
        {
            discoDoc = JsonDocument.Parse(discoJson!);
        }
        catch
        {
            _audit.Emit("logout.federated.discovery.parsefail", new { provider = idpName });
            return new FederatedRedirectResult(false, null, "discovery_parse_failed");
        }

        using (discoDoc)
        {
            var root = discoDoc!.RootElement;
            string? endSession = null;
            if (root.TryGetProperty("end_session_endpoint", out var ese)) endSession = ese.GetString();
            if (string.IsNullOrWhiteSpace(endSession))
            {
                // Fallback heuristic: authority + "/v2/logout" then "/logout"
                var authority = cfg.Authority.TrimEnd('/');
                var guess1 = authority + "/v2/logout";
                var guess2 = authority + "/logout";
                endSession = guess1; // choose first guess; some providers (Auth0) use /v2/logout
                _logger.LogDebug("end_session_endpoint not published for {Provider}; using heuristic {Guess}", provider.Name, endSession);
                _audit.Emit("logout.federated.discovery.heuristic", new { provider = idpName, guess = endSession });
            }

            var state = Guid.NewGuid().ToString("N");
            var sanitizedReturn = SanitizeLocalReturn(returnUrl);
            var model = new { s = state, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ret = sanitizedReturn };
            var json = JsonSerializer.Serialize(model);
            var protectedState = _stateProtector.Protect(json);
            _cache.Set(CachePrefix + state, protectedState, TimeSpan.FromSeconds(_opts.Value.StateTtlSeconds));

            // Build callback URL
            if (!callbackBase.EndsWith('/')) callbackBase += string.Empty; // ensure no trailing slash assumption
            var callback = callbackBase + "/logout/federated-callback";

            var qp = new List<string>
            {
                "post_logout_redirect_uri=" + Uri.EscapeDataString(callback),
                "state=" + Uri.EscapeDataString(state)
            };

            // Attempt to decrypt upstream id_token for id_token_hint
            if (!string.IsNullOrEmpty(upstreamIdTokenEnc))
            {
                try
                {
                    var raw = _idTokenProtector.Unprotect(upstreamIdTokenEnc);
                    if (!string.IsNullOrEmpty(raw)) qp.Add("id_token_hint=" + Uri.EscapeDataString(raw));
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to unprotect upstream id_token for provider {Provider}", provider.Name);
                }
            }
            if (!string.IsNullOrEmpty(upstreamSid))
            {
                qp.Add("sid=" + Uri.EscapeDataString(upstreamSid));
            }

            var redirectUrl = endSession + (endSession!.Contains('?') ? '&' : '?') + string.Join('&', qp);
            _audit.Emit("logout.federated.redirect", new { provider = idpName, has_id_token_hint = qp.Any(q => q.StartsWith("id_token_hint=")), has_sid = qp.Any(q => q.StartsWith("sid=")), state = state, dur_ms = (int)(DateTimeOffset.UtcNow - startTs).TotalMilliseconds });
            return new FederatedRedirectResult(true, redirectUrl, null);
        }
    }

    public Task<FederatedCallbackValidation> ValidateCallbackAsync(string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state)) { _audit.Emit("logout.federated.callback.fail", new { reason = "missing_state" }); return Task.FromResult(new FederatedCallbackValidation(false, "missing_state")); }
        if (!_cache.TryGetValue(CachePrefix + state, out string? protectedState))
        {
            _audit.Emit("logout.federated.callback.fail", new { reason = "state_not_found" });
            return Task.FromResult(new FederatedCallbackValidation(false, "state_not_found"));
        }
        _cache.Remove(CachePrefix + state); // single use
        try
        {
            var json = _stateProtector.Unprotect(protectedState!);
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("s", out var sEl) || sEl.GetString() != state)
            {
                _audit.Emit("logout.federated.callback.fail", new { reason = "state_mismatch" });
                return Task.FromResult(new FederatedCallbackValidation(false, "state_mismatch"));
            }
            _audit.Emit("logout.federated.callback.ok", new { });
            return Task.FromResult(new FederatedCallbackValidation(true, null));
        }
        catch
        {
            _audit.Emit("logout.federated.callback.fail", new { reason = "unprotect_failed" });
            return Task.FromResult(new FederatedCallbackValidation(false, "unprotect_failed"));
        }
    }

    private static string SanitizeLocalReturn(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        if (Uri.TryCreate(url, UriKind.Relative, out _)) return url; // keep relative
        return "/"; // disallow absolute external
    }
}
