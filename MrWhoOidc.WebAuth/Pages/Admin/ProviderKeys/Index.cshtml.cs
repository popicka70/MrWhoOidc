using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using System.Security.Cryptography;
using MrWhoOidc.Auth.Crypto;

namespace MrWhoOidc.WebAuth.Pages.Admin.ProviderKeys;

[Authorize(Policy = "admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public sealed record Row(Guid Id, string Purpose, string Alg, string? Kid, bool Active, DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt);

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public Guid ProviderId { get; private set; }
    public string ProviderDisplay { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public string? Message { get; private set; }

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
            Input.JwkJson = jwkJson!;
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
                    throw new InvalidOperationException("JWK must be a JSON object.") ;
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

        // kid uniqueness per provider
        var kidExists = await db.IdentityProviderKeys.AnyAsync(k => k.IdentityProviderId == providerId && k.Kid == Input.Kid);
        if (kidExists)
        {
            ModelState.AddModelError("Input.Kid", "Key ID (kid) already exists for this provider.");
            await LoadAsync();
            return Page();
        }

        var entity = new IdentityProviderKey
        {
            IdentityProviderId = providerId,
            Purpose = Enum.TryParse<IdentityProviderKeyPurpose>(Input.Purpose, out var p) ? p : IdentityProviderKeyPurpose.Signing,
            Jwk = Input.JwkJson!,
            Alg = Input.Alg ?? InferAlgFromJwk(Input.JwkJson!),
            Active = Input.Active,
            Kid = string.IsNullOrWhiteSpace(Input.Kid) ? null : Input.Kid,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = null
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

    private async Task LoadAsync()
    {
        Rows = await db.IdentityProviderKeys.AsNoTracking()
            .Where(k => k.IdentityProviderId == ProviderId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new Row(k.Id, k.Purpose.ToString(), k.Alg, k.Kid, k.Active, k.CreatedAt, k.ExpiresAt))
            .ToListAsync();
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
        [Required]
        public string JwkJson { get; set; } = string.Empty; // private JWK JSON or PEM
    }
}
