using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Text.Json;

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
    Task<FederatedRedirectResult> BuildFederatedRedirectAsync(ClaimsPrincipal principal, string? returnUrl, CancellationToken ct);
    Task<FederatedCallbackValidation> ValidateCallbackAsync(string? state, CancellationToken ct);
}

internal sealed class UpstreamLogoutService : IUpstreamLogoutService
{
    private readonly IMemoryCache _cache;
    private readonly IOptions<FederatedLogoutOptions> _opts;
    private readonly ILogger<UpstreamLogoutService> _logger;
    private readonly IDataProtector _protector;

    private const string CachePrefix = "fedlogout_state_";

    public UpstreamLogoutService(IMemoryCache cache, IOptions<FederatedLogoutOptions> opts, IDataProtectionProvider dp, ILogger<UpstreamLogoutService> logger)
    {
        _cache = cache; _opts = opts; _logger = logger; _protector = dp.CreateProtector("federated-logout-state");
    }

    public Task<FederatedCapability> CanFederateAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        if (!_opts.Value.Enabled) return Task.FromResult(new FederatedCapability(false, null, null));
        var idp = principal?.Claims.FirstOrDefault(c => c.Type == "idp")?.Value;
        if (string.IsNullOrEmpty(idp)) return Task.FromResult(new FederatedCapability(false, null, null));
        // Placeholder: we would look up provider config & discovery info to ensure end_session_endpoint exists.
        // For now assume presence of idp claim means we can federate (caller ensures only set for capable providers).
        return Task.FromResult(new FederatedCapability(true, idp, idp));
    }

    public Task<FederatedRedirectResult> BuildFederatedRedirectAsync(ClaimsPrincipal principal, string? returnUrl, CancellationToken ct)
    {
        var idp = principal?.Claims.FirstOrDefault(c => c.Type == "idp")?.Value;
        if (string.IsNullOrEmpty(idp)) return Task.FromResult(new FederatedRedirectResult(false, null, "missing_idp"));

        // TODO: fetch provider's discovered end_session_endpoint; placeholder base URL
        var endSession = $"https://{idp}.example.com/oidc/logout"; // placeholder

        var state = Guid.NewGuid().ToString("N");
        var model = new { s = state, ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ret = SanitizeLocalReturn(returnUrl) };
        var json = JsonSerializer.Serialize(model);
        var protectedState = _protector.Protect(json);
        _cache.Set(CachePrefix + state, protectedState, TimeSpan.FromSeconds(_opts.Value.StateTtlSeconds));

        // Build redirect (no id_token_hint for now; placeholder for sid)
        var redirectUrl = endSession + "?post_logout_redirect_uri=" + Uri.EscapeDataString($"https://localhost:5001/logout/federated-callback") + "&state=" + Uri.EscapeDataString(state);
        return Task.FromResult(new FederatedRedirectResult(true, redirectUrl, null));
    }

    public Task<FederatedCallbackValidation> ValidateCallbackAsync(string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state)) return Task.FromResult(new FederatedCallbackValidation(false, "missing_state"));
        if (!_cache.TryGetValue(CachePrefix + state, out string? protectedState))
        {
            return Task.FromResult(new FederatedCallbackValidation(false, "state_not_found"));
        }
        _cache.Remove(CachePrefix + state); // single use
        try
        {
            var json = _protector.Unprotect(protectedState!);
            var doc = JsonDocument.Parse(json);
            // Basic structure check
            if (!doc.RootElement.TryGetProperty("s", out var sEl) || sEl.GetString() != state)
                return Task.FromResult(new FederatedCallbackValidation(false, "state_mismatch"));
            return Task.FromResult(new FederatedCallbackValidation(true, null));
        }
        catch
        {
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
