using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;

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

internal sealed class OboPolicyService(AuthDbContext db, IOptions<AuthOptions> authOptions) : IOboPolicyService
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

        // If disabled explicitly, block
        if (client.OboEnabled == false)
            return (false, "unauthorized_client", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Allowed callers list
        var allowedCallers = Parse(client.OboAllowedCallersJson);
        if (allowedCallers.Length > 0 && !allowedCallers.Contains(callerClientId, StringComparer.Ordinal))
            return (false, "unauthorized_client", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Allowed target audience: if per-client set exists, enforce containment
        string[] allowedTargetAudiences = Parse(client.OboAllowedTargetAudiencesJson);
        if (allowedTargetAudiences.Length == 0)
        {
            // fallback to global ApiAudiences
            allowedTargetAudiences = authOptions.Value.ApiAudiences ?? Array.Empty<string>();
        }
        if (allowedTargetAudiences.Length > 0 && !allowedTargetAudiences.Contains(targetAudience, StringComparer.Ordinal))
            return (false, "invalid_target", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Allowed source audience (if present on subject): if allow-list configured, enforce
        var allowedSourceAudiences = Parse(client.OboAllowedSourceAudiencesJson);
        if (!string.IsNullOrEmpty(sourceAudience) && allowedSourceAudiences.Length > 0 && !allowedSourceAudiences.Contains(sourceAudience!, StringComparer.Ordinal))
            return (false, "invalid_grant", 400, Array.Empty<string>(), TimeSpan.Zero);

        // Scopes: requested ∩ subject ∩ allowed (if configured)
        var allowedScopes = Parse(client.OboAllowedScopesJson);
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

    static string[] Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }
}
