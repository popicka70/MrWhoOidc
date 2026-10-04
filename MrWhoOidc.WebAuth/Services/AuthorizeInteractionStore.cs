using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Distributed;
using MrWhoOidc.Auth.Protocols;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Server-side record of an interactive step (login, account selection, consent) that /authorize started for a
/// request object (JAR) or pushed authorization request (PAR) in this browser.
/// </summary>
/// <param name="StartedAt">Unix seconds when /authorize first sent the user to an interaction for this request.</param>
/// <param name="ConsentGivenAt">Unix seconds when the user completed the consent screen for this request, if any.</param>
public sealed record AuthorizeInteractionMarker(long StartedAt, long? ConsentGivenAt = null);

/// <summary>
/// Tracks interactions started for JAR/PAR authorization requests so the resumed /authorize can
/// (a) treat <c>prompt=login|select_account|consent</c> carried inside the request object / PAR entry as
/// satisfied once the user completed that step, and (b) re-process the same request object without tripping the
/// JAR replay cache. Markers are bound to the browser (an HttpOnly cookie), short-lived, and removed once an
/// authorization code is issued, so a genuinely new use of the same request object is still replay-checked.
/// For plain query requests the prompt is consumed from the return URL instead (AuthorizeReturnUrlHelper).
/// </summary>
public interface IAuthorizeInteractionStore
{
    Task<AuthorizeInteractionMarker?> GetAsync(HttpContext http, string interactionKey, CancellationToken ct = default);

    /// <summary>Records that an interaction starts now, unless one is already recorded (the start time is kept).</summary>
    Task BeginAsync(HttpContext http, string interactionKey, CancellationToken ct = default);

    /// <summary>Records that the user completed consent; only updates an existing marker of this browser.</summary>
    Task MarkConsentGivenAsync(HttpContext http, string interactionKey, CancellationToken ct = default);

    /// <summary>Removes the marker (authorization finished); later uses of the same request are new uses.</summary>
    Task CompleteAsync(HttpContext http, string interactionKey, CancellationToken ct = default);
}

/// <summary>Derives the interaction key that identifies a JAR or PAR authorization request.</summary>
public static class AuthorizeInteractionKey
{
    /// <summary>
    /// <c>par:</c> + hash of the request_uri, or <c>jar:</c> + hash of the request object; null for plain query requests.
    /// </summary>
    public static string? From(string? requestUri, string? requestObject)
    {
        if (!string.IsNullOrWhiteSpace(requestUri))
        {
            return "par:" + Hash(requestUri);
        }

        if (!string.IsNullOrWhiteSpace(requestObject))
        {
            return "jar:" + Hash(requestObject);
        }

        return null;
    }

    /// <summary>Interaction key of a local /authorize return URL, or null when it is not a JAR/PAR request.</summary>
    public static string? FromReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || returnUrl[0] != '/' || returnUrl.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (!Uri.TryCreate("http://local" + returnUrl, UriKind.Absolute, out var uri)
            || !uri.AbsolutePath.EndsWith("/authorize", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        string? Last(string key) => query.TryGetValue(key, out var values) ? values.LastOrDefault() : null;
        return From(Last(OAuthConstants.Parameters.RequestUri), Last(OAuthConstants.Parameters.Request));
    }

    private static string Hash(string value) => WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary><see cref="IDistributedCache"/>-backed store (Redis when configured), bound to a browser cookie.</summary>
public sealed class DistributedAuthorizeInteractionStore : IAuthorizeInteractionStore
{
    public const string BindingCookieName = "__Host-mrwhooidc-authz";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly object BindingItemKey = new();

    private readonly IDistributedCache _cache;
    private readonly TimeProvider _time;

    public DistributedAuthorizeInteractionStore(IDistributedCache cache)
        : this(cache, TimeProvider.System)
    {
    }

    internal DistributedAuthorizeInteractionStore(IDistributedCache cache, TimeProvider time)
    {
        _cache = cache;
        _time = time;
    }

    public async Task<AuthorizeInteractionMarker?> GetAsync(HttpContext http, string interactionKey, CancellationToken ct = default)
    {
        var binding = GetBinding(http);
        if (binding is null)
        {
            return null;
        }

        var bytes = await _cache.GetAsync(CacheKey(binding, interactionKey), ct).ConfigureAwait(false);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AuthorizeInteractionMarker>(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task BeginAsync(HttpContext http, string interactionKey, CancellationToken ct = default)
    {
        if (await GetAsync(http, interactionKey, ct).ConfigureAwait(false) is not null)
        {
            return;
        }

        var binding = GetBinding(http) ?? IssueBinding(http);
        var marker = new AuthorizeInteractionMarker(_time.GetUtcNow().ToUnixTimeSeconds());
        await WriteAsync(binding, interactionKey, marker, ct).ConfigureAwait(false);
    }

    public async Task MarkConsentGivenAsync(HttpContext http, string interactionKey, CancellationToken ct = default)
    {
        var binding = GetBinding(http);
        var marker = binding is null ? null : await GetAsync(http, interactionKey, ct).ConfigureAwait(false);
        if (binding is null || marker is null)
        {
            return;
        }

        await WriteAsync(binding, interactionKey, marker with { ConsentGivenAt = _time.GetUtcNow().ToUnixTimeSeconds() }, ct).ConfigureAwait(false);
    }

    public async Task CompleteAsync(HttpContext http, string interactionKey, CancellationToken ct = default)
    {
        var binding = GetBinding(http);
        if (binding is not null)
        {
            await _cache.RemoveAsync(CacheKey(binding, interactionKey), ct).ConfigureAwait(false);
        }
    }

    private Task WriteAsync(string binding, string interactionKey, AuthorizeInteractionMarker marker, CancellationToken ct)
    {
        // The marker never outlives its original window, even when updated (consent) later.
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(marker.StartedAt).Add(Lifetime);
        if (expiresAt <= _time.GetUtcNow())
        {
            return Task.CompletedTask;
        }

        return _cache.SetAsync(
            CacheKey(binding, interactionKey),
            JsonSerializer.SerializeToUtf8Bytes(marker),
            new DistributedCacheEntryOptions { AbsoluteExpiration = expiresAt },
            ct);
    }

    private static string? GetBinding(HttpContext http)
    {
        if (http.Items.TryGetValue(BindingItemKey, out var issued) && issued is string fresh)
        {
            return fresh;
        }

        return http.Request.Cookies.TryGetValue(BindingCookieName, out var value) && IsWellFormed(value) ? value : null;
    }

    private static string IssueBinding(HttpContext http)
    {
        var value = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        http.Response.Cookies.Append(BindingCookieName, value, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true
        });
        http.Items[BindingItemKey] = value;
        return value;
    }

    private static bool IsWellFormed(string? value)
        => value is { Length: 43 } && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string CacheKey(string binding, string interactionKey)
        => "authz-interaction:" + WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(binding + "\n" + interactionKey)));
}
