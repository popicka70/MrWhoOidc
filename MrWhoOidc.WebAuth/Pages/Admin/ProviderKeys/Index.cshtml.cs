using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Cryptography;
using MrWhoOidc.Auth.Crypto;
using System.Text;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.WebAuth.Security;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;

[Authorize(Policy = "tenant-admin")]
public class IndexModel(AuthDbContext db, IPublicJwksCache jwksCache) : PageModel
{
    // Extended to include parsed kty/use for advanced JWKS visual preview
    public sealed record Row(Guid Id, string Purpose, string Alg, string? Kid, bool Active, bool Publishable, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, string? Kty, string Use);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Guid ProviderId { get; private set; }
    public string ProviderDisplay { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public string? Message { get; private set; }

    // Live preview of the JWK being edited/imported
    public JwkPreview? Preview { get; private set; }

    public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

    public async Task<IActionResult> OnGetAsync(Guid providerId)
    {
        ProviderId = providerId;
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
        if (provider is null) return NotFound();
        ProviderDisplay = provider.DisplayName ?? provider.Name;
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync(Guid providerId)
    {
        ProviderId = providerId;
        var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
        if (provider is null) return NotFound();
        ProviderDisplay = provider.DisplayName ?? provider.Name;

        if (!ModelState.IsValid)
        {
            await LoadAsync();
            return Page();
        }

        var inputText = Input.JwkJson?.Trim();
        if (string.IsNullOrWhiteSpace(inputText))
        {
            ModelState.AddModelError("Input.JwkJson", "Provide a JWK JSON or a PEM private key.");
            await LoadAsync();
            return Page();
        }

        // If PEM provided, convert to JWK
        if (inputText.StartsWith("-----BEGIN", StringComparison.OrdinalIgnoreCase))
        {
            var (ok, jwkJson, error) = TryConvertPemToJwk(inputText, string.IsNullOrWhiteSpace(Input.Kid) ? Guid.NewGuid().ToString("N") : Input.Kid!, Input.Alg);
            if (!ok)
            {
                ModelState.AddModelError("Input.JwkJson", error ?? "Failed to parse PEM");
                await LoadAsync();
                return Page();
            }
            // Align 'use' claim with selected purpose (sig/enc)
            Input.JwkJson = EnsureUseMatchesPurpose(jwkJson!, Input.Purpose);
            // Ensure kid set from conversion
            if (string.IsNullOrWhiteSpace(Input.Kid)) Input.Kid = JsonDocument.Parse(jwkJson!).RootElement.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : Input.Kid;
        }
        else
        {
            // Validate JSON and basic JWK shape
            try
            {
                using var doc = JsonDocument.Parse(Input.JwkJson!);
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("JWK must be a JSON object.");
                if (!doc.RootElement.TryGetProperty("kty", out var ktyProp))
                    throw new InvalidOperationException("Missing 'kty' in JWK.");
                var kty = ktyProp.GetString();
                if (kty is not ("RSA" or "EC"))
                    throw new InvalidOperationException("Unsupported 'kty'. Only RSA and EC are accepted.");
                // Required params by kty
                if (kty == "RSA")
                {
                    if (!doc.RootElement.TryGetProperty("n", out _) || !doc.RootElement.TryGetProperty("e", out _))
                        throw new InvalidOperationException("RSA JWK must contain 'n' and 'e'.");
                }
                if (kty == "EC")
                {
                    if (!doc.RootElement.TryGetProperty("crv", out _) || !doc.RootElement.TryGetProperty("x", out _) || !doc.RootElement.TryGetProperty("y", out _))
                        throw new InvalidOperationException("EC JWK must contain 'crv', 'x' and 'y'.");
                }
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("Input.JwkJson", $"Invalid JWK: {ex.Message}");
                await LoadAsync();
                return Page();
            }
        }

        if (string.IsNullOrWhiteSpace(Input.Kid))
            Input.Kid = Guid.NewGuid().ToString("N");

        // kid uniqueness per provider (case-insensitive + robust against InMemory provider translation quirks)
        var normalizedKid = Input.Kid?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedKid))
        {
            var existingKids = await db.IdentityProviderKeys
                .Where(k => k.IdentityProviderId == providerId)
                .Select(k => k.Kid)
                .ToListAsync();
            if (existingKids.Any(k => k is not null && string.Equals(k.Trim(), normalizedKid, StringComparison.OrdinalIgnoreCase)))
            {
                ModelState.AddModelError("Input.Kid", "Key ID (kid) already exists for this provider.");
                await LoadAsync();
                return Page();
            }
        }

        // Validate algorithm/kty/use consistency and compute preview
        if (!TryValidateAlgKtyUse(Input.Purpose, Input.Alg, Input.JwkJson!, out var validationError, out var preview))
        {
            ModelState.AddModelError("Input.Alg", validationError!);
            Preview = preview; // show what we parsed to help user fix
            await LoadAsync();
            return Page();
        }
        Preview = preview;

        var entity = new IdentityProviderKey
        {
            IdentityProviderId = providerId,
            Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(Input.Purpose, out var p) ? p : IdentityProviderKeyPurpose.Signing,
            Jwk = Input.JwkJson!,
            Alg = Input.Alg ?? InferAlgFromJwk(Input.JwkJson!),
            Active = Input.Active,
            Publishable = Input.Publishable,
            Kid = string.IsNullOrWhiteSpace(Input.Kid) ? null : Input.Kid,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = Input.ExpiresAt
        };
        db.IdentityProviderKeys.Add(entity);

        if (Input.Active)
        {
            // Deactivate other keys of same purpose when marking this one active
            var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync();
            foreach (var o in others) o.Active = false;
        }

        await db.SaveChangesAsync();
        Message = "Key imported.";
        ModelState.Clear();
        Input = new();
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPrettyAsync(Guid providerId)
    {
        ProviderId = providerId;
        if (!string.IsNullOrWhiteSpace(Input.JwkJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(Input.JwkJson);
                Input.JwkJson = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
            }
            catch (Exception ex)
            {
                Error = $"Invalid JSON: {ex.Message}";
            }
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCompactAsync(Guid providerId)
    {
        ProviderId = providerId;
        if (!string.IsNullOrWhiteSpace(Input.JwkJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(Input.JwkJson);
                Input.JwkJson = JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false });
            }
            catch (Exception ex)
            {
                Error = $"Invalid JSON: {ex.Message}";
            }
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostActivateAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is null) return NotFound();
        var others = await db.IdentityProviderKeys.Where(k => k.IdentityProviderId == providerId && k.Purpose == entity.Purpose && k.Id != entity.Id).ToListAsync();
        foreach (var o in others) o.Active = false;
        entity.Active = true;
        // Invalidate provider cache so new active key appears (if publishable) or old ETag changes
        jwksCache.InvalidateProvider(providerId.ToString());
        jwksCache.InvalidateAllProviders();
        await db.SaveChangesAsync();
        Message = "Key activated.";
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is not null)
        {
            db.IdentityProviderKeys.Remove(entity);
            await db.SaveChangesAsync();
            Message = "Key deleted.";
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPublishAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is null) return NotFound();
        if (!entity.Publishable)
        {
            entity.Publishable = true;
            await db.SaveChangesAsync();
            jwksCache.InvalidateProvider(providerId.ToString());
            jwksCache.InvalidateAllProviders();
            Message = "Key marked publishable.";
        }
        await LoadAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostUnpublishAsync(Guid id, Guid providerId)
    {
        ProviderId = providerId;
        var entity = await db.IdentityProviderKeys.FirstOrDefaultAsync(k => k.Id == id && k.IdentityProviderId == providerId);
        if (entity is null) return NotFound();

        // Guard: if this is the active signing key and provider requires JAR, block unpublish to avoid breaking request signing.
        if (entity.Publishable && entity.Active && entity.Purpose == IdentityProviderKeyPurpose.Signing)
        {
            var provider = await db.IdentityProviders.AsNoTracking().FirstOrDefaultAsync(p => p.Id == providerId);
            if (provider is not null && !string.IsNullOrWhiteSpace(provider.ConfigJson) && OidcProviderConfig.TryParse(provider.ConfigJson!, out var cfg).ok && cfg is not null && cfg.UseJAR)
            {
                // Currently active signing key is the only active one (activation logic enforces single active). Reject.
                Error = "Cannot unpublish the active signing key while JAR is enabled. Import and publish a replacement (then activate) or disable JAR first.";
                await LoadAsync();
                return Page();
            }
        }

        if (entity.Publishable)
        {
            entity.Publishable = false;
            await db.SaveChangesAsync();
            jwksCache.InvalidateProvider(providerId.ToString());
            jwksCache.InvalidateAllProviders();
            Message = "Key unpublished.";
        }
        await LoadAsync();
        return Page();
    }

    private async Task LoadAsync()
    {
        // Fetch then parse JWK shape to extract kty/use (use may be absent; derive from Purpose)
        var entities = await db.IdentityProviderKeys.AsNoTracking()
            .Where(k => k.IdentityProviderId == ProviderId)
            // Order: active & publishable first, then active non-publishable, then inactive publishable (staged), then inactive non-publishable, within each by newest
            .OrderByDescending(k => k.Active)
            .ThenByDescending(k => k.Publishable)
            .ThenByDescending(k => k.CreatedAt)
            .ToListAsync();

        var rows = new List<Row>(entities.Count);
        foreach (var k in entities)
        {
            string? kty = null;
            string use = k.Purpose == IdentityProviderKeyPurpose.Encryption ? "enc" : "sig";
            try
            {
                if (!string.IsNullOrWhiteSpace(k.Jwk))
                {
                    using var doc = JsonDocument.Parse(k.Jwk);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("kty", out var ktyEl)) kty = ktyEl.GetString();
                    if (root.TryGetProperty("use", out var useEl) && !string.IsNullOrWhiteSpace(useEl.GetString())) use = useEl.GetString()!;
                }
            }
            catch { /* ignore parse errors; keep defaults */ }
            rows.Add(new Row(k.Id, k.Purpose.ToString(), k.Alg, k.Kid, k.Active, k.Publishable, k.CreatedAt, k.ExpiresAt, kty, use));
        }
        Rows = rows;
    }

    private static string InferAlgFromJwk(string jwkJson)
    {
        using var doc = JsonDocument.Parse(jwkJson);
        if (doc.RootElement.TryGetProperty("alg", out var algEl) && !string.IsNullOrWhiteSpace(algEl.GetString()))
            return algEl.GetString()!;
        var kty = doc.RootElement.TryGetProperty("kty", out var ktyEl) ? ktyEl.GetString() : null;
        if (string.Equals(kty, "EC", StringComparison.OrdinalIgnoreCase))
        {
            var crv = doc.RootElement.TryGetProperty("crv", out var crvEl) ? crvEl.GetString() : null;
            return crv switch
            {
                "P-256" => "ES256",
                "P-384" => "ES384",
                "P-521" => "ES512",
                _ => "ES256"
            };
        }
        return "RS256";
    }

    private static (bool Ok, string? JwkJson, string? Error) TryConvertPemToJwk(string pem, string kid, string alg)
    {
        try
        {
            // Try RSA first
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                var jwk = RsaJwk.FromRSA(rsa, kid, string.IsNullOrWhiteSpace(alg) ? "RS256" : alg, includePrivate: true);
                return (true, jwk.ToJson(includePrivate: true), null);
            }
            catch { /* fall-through to EC */ }

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);
            var inferredAlg = string.IsNullOrWhiteSpace(alg) ? InferEcAlg(ecdsa) : alg;
            var ec = EcJwk.FromECDsa(ecdsa, kid, inferredAlg, includePrivate: true);
            return (true, ec.ToJson(includePrivate: true), null);
        }
        catch (Exception ex)
        {
            return (false, null, ex.Message);
        }
    }

    private static string InferEcAlg(ECDsa ec)
    {
        var curve = ec.ExportParameters(true).Curve.Oid.FriendlyName;
        return curve switch
        {
            "nistP256" or "ECDSA_P256" or "prime256v1" => "ES256",
            "nistP384" or "ECDSA_P384" => "ES384",
            "nistP521" or "ECDSA_P521" => "ES512",
            _ => "ES256"
        };
    }

    public sealed class InputModel
    {
        [Required]
        public string Purpose { get; set; } = "Signing"; // enum name
        [Required, StringLength(20)]
        public string Alg { get; set; } = "RS256";
        [StringLength(200)]
        public string? Kid { get; set; }
        public bool Active { get; set; } = true;
        public bool Publishable { get; set; } = true; // default: publish new key unless explicitly staged
        [Required]
        public string JwkJson { get; set; } = string.Empty; // private JWK JSON or PEM
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    // === Preview + validation helpers ===
    public sealed record JwkPreview(string? Kid, string? Alg, string? Kty, string? Use, string? Curve, string ThumbprintB64Url);

    private static string EnsureUseMatchesPurpose(string jwkJson, string purpose)
    {
        try
        {
            using var doc = JsonDocument.Parse(jwkJson);
            var root = doc.RootElement;
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            string desiredUse = string.Equals(purpose, nameof(IdentityProviderKeyPurpose.Encryption), StringComparison.OrdinalIgnoreCase) ? "enc" : "sig";
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.NameEquals("use")) continue; // we'll write our own
                prop.WriteTo(writer);
            }
            writer.WriteString("use", desiredUse);
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch { return jwkJson; }
    }

    private static bool TryValidateAlgKtyUse(string purpose, string alg, string jwkJson, out string? error, out JwkPreview? preview)
    {
        error = null;
        preview = null;
        using var doc = JsonDocument.Parse(jwkJson);
        var root = doc.RootElement;
        var kty = root.TryGetProperty("kty", out var ktyEl) ? ktyEl.GetString() : null;
        var use = root.TryGetProperty("use", out var useEl) ? useEl.GetString() : null;
        var kid = root.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
        var crv = root.TryGetProperty("crv", out var crvEl) ? crvEl.GetString() : null;

        var expectedUse = string.Equals(purpose, nameof(IdentityProviderKeyPurpose.Encryption), StringComparison.OrdinalIgnoreCase) ? "enc" : "sig";
        if (!string.IsNullOrWhiteSpace(use) && !string.Equals(use, expectedUse, StringComparison.Ordinal))
        {
            error = $"JWK 'use' is '{use}', but purpose is '{purpose}'. Expected use='{expectedUse}'.";
        }

        // Normalize alg for checks
        var A = alg?.Trim() ?? string.Empty;
        static bool IsRsaSig(string a) => a.StartsWith("RS", StringComparison.OrdinalIgnoreCase) || a.StartsWith("PS", StringComparison.OrdinalIgnoreCase);
        static bool IsEcSig(string a) => a.StartsWith("ES", StringComparison.OrdinalIgnoreCase);
        static bool IsRsaEnc(string a) => a.StartsWith("RSA-OAEP", StringComparison.OrdinalIgnoreCase);
        static bool IsEcEnc(string a) => a.StartsWith("ECDH-ES", StringComparison.OrdinalIgnoreCase);

        // kty vs alg
        if (string.Equals(expectedUse, "sig", StringComparison.Ordinal))
        {
            if (IsRsaSig(A) && !string.Equals(kty, "RSA", StringComparison.Ordinal))
                error ??= $"Alg '{alg}' requires kty=RSA.";
            if (IsEcSig(A) && !string.Equals(kty, "EC", StringComparison.Ordinal))
                error ??= $"Alg '{alg}' requires kty=EC.";

            if (IsEcSig(A) && string.Equals(kty, "EC", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(crv))
            {
                // ES256 -> P-256, ES384 -> P-384, ES512 -> P-521
                var expectedCrv = A switch
                {
                    "ES256" => "P-256",
                    "ES384" => "P-384",
                    "ES512" => "P-521",
                    _ => null
                };
                if (expectedCrv is not null && !string.Equals(crv, expectedCrv, StringComparison.Ordinal))
                {
                    error ??= $"Alg '{alg}' expects EC curve '{expectedCrv}', but JWK has '{crv}'.";
                }
            }
        }
        else // enc
        {
            if (IsRsaEnc(A) && !string.Equals(kty, "RSA", StringComparison.Ordinal))
                error ??= $"Alg '{alg}' requires kty=RSA.";
            if (IsEcEnc(A) && !string.Equals(kty, "EC", StringComparison.Ordinal))
                error ??= $"Alg '{alg}' requires kty=EC.";
        }

        // Compute thumbprint for preview
        var thumb = ComputeJwkThumbprintB64Url(root);
        preview = new JwkPreview(kid, A, kty, string.IsNullOrWhiteSpace(use) ? expectedUse : use, crv, thumb);
        return error is null;
    }

    private static string ComputeJwkThumbprintB64Url(JsonElement jwk)
    {
        // RFC 7638: canonical JSON with required public members in lexicographic order
        string json;
        if (jwk.TryGetProperty("kty", out var ktyEl) && string.Equals(ktyEl.GetString(), "RSA", StringComparison.Ordinal))
        {
            var e = jwk.TryGetProperty("e", out var eEl) ? eEl.GetString() ?? string.Empty : string.Empty;
            var n = jwk.TryGetProperty("n", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty;
            json = "{" + "\"e\":\"" + e + "\",\"kty\":\"RSA\",\"n\":\"" + n + "\"}";
        }
        else // EC
        {
            var crv = jwk.TryGetProperty("crv", out var crvEl) ? crvEl.GetString() ?? string.Empty : string.Empty;
            var x = jwk.TryGetProperty("x", out var xEl) ? xEl.GetString() ?? string.Empty : string.Empty;
            var y = jwk.TryGetProperty("y", out var yEl) ? yEl.GetString() ?? string.Empty : string.Empty;
            json = "{" + "\"crv\":\"" + crv + "\",\"kty\":\"EC\",\"x\":\"" + x + "\",\"y\":\"" + y + "\"}";
        }
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = sha.ComputeHash(bytes);
        return MrWhoOidc.Auth.Crypto.Base64Url.Encode(hash);
    }
}
