using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace MrWhoOidc.ApiService;

public interface IDPoPValidator
{
    Task<DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default);
}

public readonly record struct DPoPValidationResult(bool Ok, string? Jkt, string? Jti, long? Iat, string? Nonce, string? Error);

public interface IDPoPReplayCache
{
    bool TryAdd(string key, DateTimeOffset expiresAt);
}

public interface IDPoPNonceStore
{
    Task<(bool ok, string nonce)> ValidateOrIssueAsync(string endpoint, string clientIp, string? jkt, string? provided, CancellationToken ct = default);
}

public sealed class DPoPValidator : IDPoPValidator
{
    private static readonly string[] AllowedAlgs = [SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.RsaSha256];
    public Task<DPoPValidationResult> ValidateForEndpointAsync(HttpContext http, string absoluteEndpointUrl, string? accessToken = null, CancellationToken ct = default)
    {
        var header = http.Request.Headers["DPoP"].ToString();
        if (string.IsNullOrWhiteSpace(header)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "missing_dpop"));
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var unsigned = handler.ReadJwtToken(header);
            if (!string.Equals(unsigned.Header.Typ, "dpop+jwt", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "invalid_typ"));

            // Read jwk from raw header to avoid dynamic typing issues
            var headerBytes = Base64UrlEncoder.DecodeBytes(unsigned.EncodedHeader);
            using var json = System.Text.Json.JsonDocument.Parse(headerBytes);
            if (!json.RootElement.TryGetProperty("jwk", out var jwkElement))
                return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "missing_jwk"));

            var key = CreateSecurityKeyFromJwk(jwkElement);
            if (key is null) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "unsupported_jwk"));
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidateAudience = false,
                ValidateIssuer = false,
                ValidateLifetime = false,
                ValidAlgorithms = AllowedAlgs
            };
            handler.ValidateToken(header, parameters, out var validatedToken);
            var jwt = (JwtSecurityToken)validatedToken;
            var htm = jwt.Payload.TryGetValue("htm", out var htmObj) ? htmObj?.ToString() : null;
            var htu = jwt.Payload.TryGetValue("htu", out var htuObj) ? htuObj?.ToString() : null;
            var iat = jwt.Payload.TryGetValue("iat", out var iatObj) ? iatObj : null;
            var jti = jwt.Payload.TryGetValue("jti", out var jtiObj) ? jtiObj?.ToString() : null;
            var nonce = jwt.Payload.TryGetValue("nonce", out var nonceObj) ? nonceObj?.ToString() : null;
            if (string.IsNullOrEmpty(htm) || string.IsNullOrEmpty(htu) || string.IsNullOrEmpty(jti)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "missing_claims"));
            if (!string.Equals(htm, http.Request.Method, StringComparison.OrdinalIgnoreCase)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "htm_mismatch"));
            var expected = new Uri(absoluteEndpointUrl).GetLeftPart(UriPartial.Path).TrimEnd('/');
            var provided = new Uri(htu).GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!string.Equals(expected, provided, StringComparison.Ordinal)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "htu_mismatch"));
            if (iat is null || !long.TryParse(iat.ToString(), out var iatSec)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "invalid_iat"));
            var iatTime = DateTimeOffset.FromUnixTimeSeconds(iatSec);
            var now = DateTimeOffset.UtcNow;
            if (iatTime < now.AddMinutes(-5) || iatTime > now.AddMinutes(5)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "iat_out_of_range"));
            if (!string.IsNullOrEmpty(accessToken))
            {
                var ath = jwt.Payload.TryGetValue("ath", out var athObj) ? athObj?.ToString() : null;
                if (string.IsNullOrEmpty(ath)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "missing_ath"));
                var tokenHash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
                var tokenHashB64Url = Base64UrlEncoder.Encode(tokenHash);
                if (!string.Equals(ath, tokenHashB64Url, StringComparison.Ordinal)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "ath_mismatch"));
            }
            var jkt = ComputeJwkThumbprint(jwkElement);
            if (string.IsNullOrEmpty(jkt)) return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, "thumbprint_error"));
            return Task.FromResult(new DPoPValidationResult(true, jkt, jti, iatSec, nonce, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new DPoPValidationResult(false, null, null, null, null, ex.Message));
        }
    }
    private static SecurityKey? CreateSecurityKeyFromJwk(System.Text.Json.JsonElement jwk)
    {
        if (!jwk.TryGetProperty("kty", out var ktyEl)) return null;
        var kty = ktyEl.GetString();
        if (string.Equals(kty, "EC", StringComparison.Ordinal))
        {
            if (!jwk.TryGetProperty("crv", out var crvEl) || !jwk.TryGetProperty("x", out var xEl) || !jwk.TryGetProperty("y", out var yEl)) return null;
            var crv = crvEl.GetString();
            var x = Base64UrlEncoder.DecodeBytes(xEl.GetString());
            var y = Base64UrlEncoder.DecodeBytes(yEl.GetString());
            var ecParams = new ECParameters { Q = new ECPoint { X = x, Y = y }, Curve = crv switch { "P-256" => ECCurve.NamedCurves.nistP256, "P-384" => ECCurve.NamedCurves.nistP384, "P-521" => ECCurve.NamedCurves.nistP521, _ => ECCurve.NamedCurves.nistP256 } };
            var ecdsa = ECDsa.Create();
            ecdsa.ImportParameters(ecParams);
            return new ECDsaSecurityKey(ecdsa) { KeyId = null };
        }
        if (string.Equals(kty, "RSA", StringComparison.Ordinal))
        {
            if (!jwk.TryGetProperty("n", out var nEl) || !jwk.TryGetProperty("e", out var eEl)) return null;
            var n = Base64UrlEncoder.DecodeBytes(nEl.GetString());
            var e = Base64UrlEncoder.DecodeBytes(eEl.GetString());
            var rsa = RSA.Create();
            rsa.ImportParameters(new RSAParameters { Modulus = n, Exponent = e });
            return new RsaSecurityKey(rsa) { KeyId = null };
        }
        return null;
    }
    private static string ComputeJwkThumbprint(System.Text.Json.JsonElement jwk)
    {
        if (!jwk.TryGetProperty("kty", out var ktyEl)) return string.Empty;
        var kty = ktyEl.GetString();
        string json;
        if (string.Equals(kty, "EC", StringComparison.Ordinal))
        {
            if (!jwk.TryGetProperty("crv", out var crvEl) || !jwk.TryGetProperty("x", out var xEl) || !jwk.TryGetProperty("y", out var yEl)) return string.Empty;
            json = $"{{\"crv\":\"{crvEl.GetString()}\",\"kty\":\"EC\",\"x\":\"{xEl.GetString()}\",\"y\":\"{yEl.GetString()}\"}}";
        }
        else if (string.Equals(kty, "RSA", StringComparison.Ordinal))
        {
            if (!jwk.TryGetProperty("e", out var eEl) || !jwk.TryGetProperty("n", out var nEl)) return string.Empty;
            json = $"{{\"e\":\"{eEl.GetString()}\",\"kty\":\"RSA\",\"n\":\"{nEl.GetString()}\"}}";
        }
        else return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);
        return Base64UrlEncoder.Encode(hash);
    }
}

public sealed class InMemoryDPoPReplayCache : IDPoPReplayCache
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _store = new(StringComparer.Ordinal);
    public bool TryAdd(string key, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (_store.TryGetValue(key, out var existing))
        {
            if (existing > now) return false;
            _store.TryRemove(key, out _);
        }
        return _store.TryAdd(key, expiresAt);
    }
}

public sealed class InMemoryDPoPNonceStore : IDPoPNonceStore
{
    private record Entry(string Nonce, DateTimeOffset ExpiresAt);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(2);
    public Task<(bool ok, string nonce)> ValidateOrIssueAsync(string endpoint, string clientIp, string? jkt, string? provided, CancellationToken ct = default)
    {
        Cleanup();
        var key = Key(endpoint, clientIp, jkt);
        if (!_store.TryGetValue(key, out var entry) || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            var nonce = CreateNonce();
            _store[key] = new Entry(nonce, DateTimeOffset.UtcNow.Add(Ttl));
            return Task.FromResult((false, nonce));
        }
        if (string.IsNullOrEmpty(provided) || !string.Equals(provided, entry.Nonce, StringComparison.Ordinal))
        {
            var nonce = CreateNonce();
            _store[key] = new Entry(nonce, DateTimeOffset.UtcNow.Add(Ttl));
            return Task.FromResult((false, nonce));
        }
        return Task.FromResult((true, entry.Nonce));
    }
    static string Key(string endpoint, string clientIp, string? jkt) => $"dpop:nonce:{endpoint}:{clientIp}:{(jkt ?? "no")}";
    static string CreateNonce() => Convert.ToBase64String(Guid.NewGuid().ToByteArray()).TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
    void Cleanup()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var kv in _store)
        {
            if (kv.Value.ExpiresAt <= now)
            {
                _store.TryRemove(kv.Key, out _);
            }
        }
    }
}
