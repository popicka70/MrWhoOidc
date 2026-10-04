using System.Text.Json;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.Auth.Services;

public interface IOboPolicyService
{
    // Returns (ok, error, status, effectiveScopes, lifetime, dpopAllowed)
    Task<(bool ok, string? error, int status, string[] scopes, TimeSpan lifetime)> EvaluateAsync(
        string callerClientId,
        string? sourceAudience,
        string targetAudience,
        string[] subjectScopes,
        string[] requestedScopes,
        DateTimeOffset subjectExpiry,
        CancellationToken ct = default);
}

internal sealed class OboPolicyService(AuthDbContext db, IOptions<AuthOptions> authOptions, ILogger<OboPolicyService>? logger = null) : IOboPolicyService
{
    public async Task<(bool ok, string? error, int status, string[] scopes, TimeSpan lifetime)> EvaluateAsync(
        string callerClientId,
        string? sourceAudience,
        string targetAudience,
        string[] subjectScopes,
        string[] requestedScopes,
        DateTimeOffset subjectExpiry,
        CancellationToken ct = default)
    {
        // Load caller client
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientId == callerClientId, ct).ConfigureAwait(false);
        if (client is null)
            return (false, "unauthorized_client", 400, Array.Empty<string>(), TimeSpan.Zero);

        // If disabled explicitly, block. OboEnabled == null (never configured) stays "enabled": token exchange is
        // still bounded by the target-audience allow-list below, which now fails closed when nothing is configured.
        if (client.OboEnabled == false)
            return (false, "unauthorized_client", 400, Array.Empty<string>(), TimeSpan.Zero);

        // An allow-list that does not parse must never read as "no restriction" (R25): deny until it is fixed.
        // OboAllowedCallersJson (the clients whose tokens this client may exchange) is enforced against the subject
        // token's client by TokenExchangeService; it is only validated here. The former check compared the caller
        // with the caller's own list, which never restricted anything.
        if (!TryParse(client.OboAllowedCallersJson, out _)
            || !TryParse(client.OboAllowedTargetAudiencesJson, out var allowedTargetAudiences)
            || !TryParse(client.OboAllowedSourceAudiencesJson, out var allowedSourceAudiences)
            || !TryParse(client.OboAllowedScopesJson, out var allowedScopes))
        {
            logger?.LogWarning("Token exchange denied for client {ClientId}: its OBO allow-list configuration is not a valid JSON string array", callerClientId);
            return (false, "unauthorized_client", 400, Array.Empty<string>(), TimeSpan.Zero);
        }

        // Allowed target audience: the per-client list, else the global ApiAudiences. With neither configured there
        // is nothing the caller may target, so the exchange is denied rather than allowed for any audience.
        if (allowedTargetAudiences.Length == 0)
        {
            allowedTargetAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
        }
        if (!allowedTargetAudiences.Contains(targetAudience, StringComparer.Ordinal))
            return (false, "invalid_target", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Allowed source audience (if present on subject): if allow-list configured, enforce
        if (!string.IsNullOrEmpty(sourceAudience) && allowedSourceAudiences.Length > 0 && !allowedSourceAudiences.Contains(sourceAudience!, StringComparer.Ordinal))
            return (false, "invalid_grant", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Scopes: requested ∩ subject ∩ allowed (if configured)
        // Protected scopes must be explicitly listed in OboAllowedScopesJson.
        // This prevents accidental enablement when allowedScopes is empty (meaning "allow any").
        var protectsTenantsScope = true;
        if (protectsTenantsScope)
        {
            var isRequestingTenants = (requestedScopes is { Length: > 0 } && requestedScopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal))
                || (requestedScopes is not { Length: > 0 } && subjectScopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal));

            if (isRequestingTenants && (allowedScopes.Length == 0 || !allowedScopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal)))
            {
                return (false, "insufficient_scope", 400, Array.Empty<string>(), TimeSpan.Zero);
            }
        }
        HashSet<string> granted = new(StringComparer.Ordinal);
        var subjectSet = new HashSet<string>(subjectScopes, StringComparer.Ordinal);
        if (requestedScopes is { Length: > 0 })
        {
            foreach (var s in requestedScopes)
            {
                if (!subjectSet.Contains(s)) continue;
                if (allowedScopes.Length == 0 || allowedScopes.Contains(s, StringComparer.Ordinal))
                    granted.Add(s);
            }
        }
        else
        {
            foreach (var s in subjectScopes)
            {
                if (allowedScopes.Length == 0 || allowedScopes.Contains(s, StringComparer.Ordinal))
                    granted.Add(s);
            }
        }
        if (granted.Count == 0) return (false, "insufficient_scope", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Lifetime: min(subject remaining, client policy, server cap 15m)
        var now = DateTimeOffset.UtcNow;
        var remaining = subjectExpiry - now;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        var policyMinutes = client.OboMaxLifetimeMinutes.HasValue && client.OboMaxLifetimeMinutes.Value > 0
            ? TimeSpan.FromMinutes(client.OboMaxLifetimeMinutes.Value)
            : TimeSpan.FromMinutes(15);
        var lifetime = remaining <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : (remaining < policyMinutes ? remaining : policyMinutes);

        return (true, null, 200, granted.ToArray(), lifetime);
    }

    /// <summary>Empty/whitespace is an unset list (true, empty); anything that is not a JSON string array is false.</summary>
    internal static bool TryParse(string? json, out string[] values)
    {
        values = Array.Empty<string>();
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            values = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
