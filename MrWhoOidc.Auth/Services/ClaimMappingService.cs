using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for mapping external identity provider claims to local claims.
/// </summary>
public interface IClaimMappingService
{
    /// <summary>
    /// Applies claim mappings for a specific provider to a set of source claims.
    /// </summary>
    /// <param name="providerId">The ID of the identity provider.</param>
    /// <param name="source">The source claims from the external provider.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A dictionary of mapped local claims.</returns>
    Task<Dictionary<string, string>> ApplyAsync(Guid providerId, IReadOnlyDictionary<string, string?> source, CancellationToken ct = default);
}

public sealed class ClaimMappingService(AuthDbContext db, IOptions<AuthOptions> options) : IClaimMappingService
{
    private readonly AuthOptions _options = options.Value;

    public async Task<Dictionary<string, string>> ApplyAsync(Guid providerId, IReadOnlyDictionary<string, string?> source, CancellationToken ct = default)
    {
        var mappings = await db.IdentityProviderClaimMappings.AsNoTracking()
            .Where(m => m.IdentityProviderId == providerId)
            .OrderBy(m => m.Order)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Fallback to defaults from configuration when no explicit mappings are defined for this provider
        if (mappings.Count == 0 && _options.DefaultClaimMappings is { Length: > 0 })
        {
            mappings = _options.DefaultClaimMappings
                .OrderBy(r => r.Order)
                .Select(r => new IdentityProviderClaimMapping
                {
                    IdentityProviderId = providerId,
                    ExternalClaim = r.ExternalClaim,
                    LocalClaim = r.LocalClaim,
                    Transform = string.IsNullOrWhiteSpace(r.Transform) ? null : r.Transform,
                    Order = r.Order
                })
                .ToList();
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappings)
        {
            var value = ResolveValue(m, source);
            if (string.IsNullOrEmpty(value))
                continue;
            result[m.LocalClaim] = value!;
        }
        return result;
    }

    private static string? ResolveValue(IdentityProviderClaimMapping m, IReadOnlyDictionary<string, string?> source)
    {
        var transform = m.Transform ?? "copy";
        if (transform.StartsWith("concat:", StringComparison.OrdinalIgnoreCase))
        {
            // concat:claim1,claim2,claim3|sep= |
            var spec = transform.Substring("concat:".Length);
            string sep = "";
            var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[1].StartsWith("sep=", StringComparison.OrdinalIgnoreCase))
                sep = parts[1].Substring(4);
            var names = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            // Trim each input before joining to prevent double spaces when upstream sends padded values
            var values = names
                .Select(n => TryGet(source, n)?.Trim())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToArray();
            return string.Join(sep, values!);
        }

        var input = TryGet(source, m.ExternalClaim);
        if (input is null)
            return null;

        if (transform.Equals("copy", StringComparison.OrdinalIgnoreCase)) return input;
        if (transform.Equals("trim", StringComparison.OrdinalIgnoreCase)) return input.Trim();
        if (transform.Equals("case:lower", StringComparison.OrdinalIgnoreCase)) return input.ToLowerInvariant();
        if (transform.Equals("case:upper", StringComparison.OrdinalIgnoreCase)) return input.ToUpperInvariant();
        if (transform.StartsWith("prefix:", StringComparison.OrdinalIgnoreCase)) return transform.Substring(7) + input;
        if (transform.StartsWith("suffix:", StringComparison.OrdinalIgnoreCase)) return input + transform.Substring(7);
        if (transform.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            // regex:/pattern/replacement/flags
            var spec = transform.Substring("regex:".Length);
            if (spec.Length > 0 && spec[0] == '/')
            {
                var second = spec.IndexOf('/', 1);
                if (second > 0)
                {
                    var third = spec.IndexOf('/', second + 1);
                    if (third > 0)
                    {
                        var pattern = spec.Substring(1, second - 1);
                        var replacement = spec.Substring(second + 1, third - second - 1);
                        var flags = spec.Substring(third + 1);
                        var options = RegexOptions.None;
                        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
                        // Bound execution: the pattern is operator-configurable and the input is an
                        // attacker-influenceable upstream claim value, so a catastrophic-backtracking
                        // pattern must not be able to stall the request thread (ReDoS).
                        try
                        {
                            return Regex.Replace(input, pattern, replacement, options, TimeSpan.FromMilliseconds(100));
                        }
                        catch (RegexMatchTimeoutException)
                        {
                            return input;
                        }
                    }
                }
            }
        }
        // default: copy
        return input;
    }

    private static string? TryGet(IReadOnlyDictionary<string, string?> source, string name)
        => source.TryGetValue(name, out var v) ? v : null;
}
