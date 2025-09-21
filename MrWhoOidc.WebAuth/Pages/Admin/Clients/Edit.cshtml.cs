using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Crypto;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize]
public class EditModel(AuthDbContext db, IPasswordHasher hasher) : PageModel
{
    [FromRoute]
    public Guid Id { get; set; }

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    public List<KeyPreview> KeyPreviews { get; private set; } = new();

    public JwtValidationOutput? JwtTest { get; private set; }

    public string? GeneratedPrivateJwk { get; private set; }

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    [BindProperty]
    public string? GenerateAlg { get; set; }

    [BindProperty]
    public string? RemoveKid { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null) return NotFound();

        await LoadRealmsAsync();

        string introspectionAudiences = string.Empty;
        if (!string.IsNullOrEmpty(client.IntrospectionAudiencesJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<string[]>(client.IntrospectionAudiencesJson) ?? Array.Empty<string>();
                introspectionAudiences = string.Join(", ", list);
            }
            catch { /* ignore */ }
        }

        Input = new ClientInput
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RealmId = client.RealmId,
            RequirePkce = client.RequirePkce,
            RequireConsent = client.RequireConsent,
            IntrospectionAudiences = introspectionAudiences,
            PublicJwksJson = client.PublicJwksJson,
            PublicJwksUri = client.PublicJwksUri
        };

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);

        return Page();
    }

    public async Task<IActionResult> OnPostGenerateJwksAsync()
    {
        await LoadRealmsAsync();

        var alg = string.IsNullOrWhiteSpace(GenerateAlg) ? "RS256" : GenerateAlg!.ToUpperInvariant();
        if (alg.StartsWith("ES"))
        {
            EcJwk ecJwk;
            switch (alg)
            {
                case "ES384":
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES384", includePrivate: true);
                    }
                    break;
                case "ES512":
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP521))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES512", includePrivate: true);
                    }
                    break;
                default:
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES256", includePrivate: true);
                    }
                    break;
            }

            GeneratedPrivateJwk = ecJwk.ToJson(includePrivate: true);
            var publicJwk = new EcJwk
            {
                Kty = ecJwk.Kty,
                Kid = ecJwk.Kid,
                Alg = ecJwk.Alg,
                Use = ecJwk.Use,
                Crv = ecJwk.Crv,
                X = ecJwk.X,
                Y = ecJwk.Y,
                D = null
            };
            var jwks = new { keys = new[] { publicJwk } };
            Input.PublicJwksJson = JsonSerializer.Serialize(jwks, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var kid = Guid.NewGuid().ToString("N");
            var jwk = RsaJwk.FromRSA(rsa, kid, alg: "RS256", includePrivate: true);
            GeneratedPrivateJwk = jwk.ToJson(includePrivate: true);
            var publicJwk = new RsaJwk
            {
                Kty = jwk.Kty,
                Kid = jwk.Kid,
                Alg = jwk.Alg,
                Use = jwk.Use,
                N = jwk.N,
                E = jwk.E,
                D = null, P = null, Q = null, DP = null, DQ = null, QI = null
            };
            var jwks = new { keys = new[] { publicJwk } };
            Input.PublicJwksJson = JsonSerializer.Serialize(jwks, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostAddKeyAsync()
    {
        await LoadRealmsAsync();
        var alg = string.IsNullOrWhiteSpace(GenerateAlg) ? "RS256" : GenerateAlg!.ToUpperInvariant();

        // Ensure we have a JWKS container
        var keysRaw = ExtractKeysRaw(Input.PublicJwksJson);

        string newPublicJwkJson;
        if (alg.StartsWith("ES"))
        {
            EcJwk ecJwk;
            switch (alg)
            {
                case "ES384":
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP384))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES384", includePrivate: true);
                    }
                    break;
                case "ES512":
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP521))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES512", includePrivate: true);
                    }
                    break;
                default:
                    using (var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256))
                    {
                        var kid = Guid.NewGuid().ToString("N");
                        ecJwk = EcJwk.FromECDsa(ec, kid, alg: "ES256", includePrivate: true);
                    }
                    break;
            }
            GeneratedPrivateJwk = ecJwk.ToJson(includePrivate: true);
            newPublicJwkJson = new EcJwk
            {
                Kty = ecJwk.Kty,
                Kid = ecJwk.Kid,
                Alg = ecJwk.Alg,
                Use = ecJwk.Use,
                Crv = ecJwk.Crv,
                X = ecJwk.X,
                Y = ecJwk.Y,
                D = null
            }.ToJson(includePrivate: false);
        }
        else
        {
            using var rsa = RSA.Create(2048);
            var kid = Guid.NewGuid().ToString("N");
            var jwk = RsaJwk.FromRSA(rsa, kid, alg: "RS256", includePrivate: true);
            GeneratedPrivateJwk = jwk.ToJson(includePrivate: true);
            newPublicJwkJson = new RsaJwk
            {
                Kty = jwk.Kty,
                Kid = jwk.Kid,
                Alg = jwk.Alg,
                Use = jwk.Use,
                N = jwk.N,
                E = jwk.E,
                D = null, P = null, Q = null, DP = null, DQ = null, QI = null
            }.ToJson(includePrivate: false);
        }

        keysRaw.Insert(0, newPublicJwkJson); // prepend newest
        Input.PublicJwksJson = ComposeJwks(keysRaw);
        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveKeyAsync()
    {
        await LoadRealmsAsync();
        var keysRaw = ExtractKeysRaw(Input.PublicJwksJson);
        if (!string.IsNullOrWhiteSpace(RemoveKid))
        {
            keysRaw = keysRaw.Where(raw =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    var kid = doc.RootElement.TryGetProperty("kid", out var kidProp) ? kidProp.GetString() : null;
                    return !string.Equals(kid, RemoveKid, StringComparison.Ordinal);
                }
                catch { return true; }
            }).ToList();
        }
        Input.PublicJwksJson = ComposeJwks(keysRaw);
        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadRealmsAsync();
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            return Page();
        }

        // Validate JWKS JSON if provided
        if (!string.IsNullOrWhiteSpace(Input.PublicJwksJson))
        {
            try
            {
                using var _ = JsonDocument.Parse(Input.PublicJwksJson);
            }
            catch
            {
                await LoadRealmsAsync();
                ModelState.AddModelError("Input.PublicJwksJson", "Invalid JSON.");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                return Page();
            }
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null) return NotFound();

        // If client id changed, enforce uniqueness
        if (!string.Equals(client.ClientId, Input.ClientId, StringComparison.Ordinal))
        {
            var exists = await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId);
            if (exists)
            {
                await LoadRealmsAsync();
                ModelState.AddModelError("Input.ClientId", "Client ID already exists");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                return Page();
            }
        }

        client.ClientId = Input.ClientId;
        client.ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName;
        client.RealmId = Input.RealmId;
        client.RequirePkce = Input.RequirePkce;
        client.RequireConsent = Input.RequireConsent;
        if (!string.IsNullOrEmpty(Input.ClientSecret))
        {
            client.ClientSecretHash = hasher.Hash(Input.ClientSecret);
        }

        // Introspection audiences: comma/space separated list -> json array
        if (!string.IsNullOrWhiteSpace(Input.IntrospectionAudiences))
        {
            var list = Input.IntrospectionAudiences
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.IntrospectionAudiencesJson = JsonSerializer.Serialize(list);
        }
        else
        {
            client.IntrospectionAudiencesJson = null; // unset
        }

        client.PublicJwksJson = string.IsNullOrWhiteSpace(Input.PublicJwksJson) ? null : Input.PublicJwksJson;
        client.PublicJwksUri = string.IsNullOrWhiteSpace(Input.PublicJwksUri) ? null : Input.PublicJwksUri;

        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostFetchJwksAsync()
    {
        await LoadRealmsAsync();
        if (string.IsNullOrWhiteSpace(Input.PublicJwksUri))
        {
            ModelState.AddModelError("Input.PublicJwksUri", "Enter a JWKS URI to fetch.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            return Page();
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = await http.GetStringAsync(Input.PublicJwksUri);
            // Column limit guard
            if (content.Length > 8000)
            {
                ModelState.AddModelError("Input.PublicJwksUri", "JWKS content too large (over 8000 characters).");
            }
            else
            {
                // Validate JSON
                using var _ = JsonDocument.Parse(content);
                Input.PublicJwksJson = content;
            }
        }
        catch
        {
            ModelState.AddModelError("Input.PublicJwksUri", "Failed to fetch JWKS from URI.");
        }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostValidateJwtAsync()
    {
        await LoadRealmsAsync();
        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwtTest = new JwtValidationOutput();

        if (string.IsNullOrWhiteSpace(Input.TestJwt))
        {
            ModelState.AddModelError("Input.TestJwt", "Paste a JWT to validate.");
            return Page();
        }

        // Determine JWKS source: prefer posted JSON, else current DB value
        string? jwksJson = Input.PublicJwksJson;
        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Id);
            jwksJson = client?.PublicJwksJson;
        }

        if (string.IsNullOrWhiteSpace(jwksJson))
        {
            ModelState.AddModelError("Input.PublicJwksJson", "Provide JWKS JSON to validate signature.");
            return Page();
        }

        IReadOnlyCollection<SecurityKey> keys;
        try
        {
            if (jwksJson.Contains("\"keys\"", StringComparison.Ordinal))
            {
                var set = new JsonWebKeySet(jwksJson);
                keys = set.Keys.Select(k => (SecurityKey)k).ToArray();
            }
            else
            {
                var jwk = new JsonWebKey(jwksJson);
                keys = new[] { (SecurityKey)jwk };
            }
        }
        catch
        {
            ModelState.AddModelError("Input.PublicJwksJson", "Invalid JWKS/JWK JSON.");
            return Page();
        }

        var handler = new JwtSecurityTokenHandler();
        JwtSecurityToken parsed;
        try
        {
            parsed = handler.ReadJwtToken(Input.TestJwt);
        }
        catch
        {
            JwtTest = new JwtValidationOutput { Ok = false, Message = "Malformed JWT." };
            return Page();
        }

        var tvp = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys,
            RequireSignedTokens = true,
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidAlgorithms = new[] { SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512, SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512 }
        };

        try
        {
            var principal = handler.ValidateToken(Input.TestJwt, tvp, out var validated);
            JwtTest = new JwtValidationOutput
            {
                Ok = true,
                Message = "Signature valid.",
                HeaderAlg = parsed.Header.Alg,
                HeaderKid = parsed.Header.TryGetValue("kid", out var kidObj) ? kidObj?.ToString() : null,
                Iss = principal.FindFirst("iss")?.Value ?? parsed.Issuer,
                Sub = principal.FindFirst("sub")?.Value,
                Aud = string.Join(" ", principal.FindAll("aud").Select(c => c.Value)),
                Iat = principal.FindFirst("iat")?.Value,
                Nbf = principal.FindFirst("nbf")?.Value,
                Exp = principal.FindFirst("exp")?.Value
            };
        }
        catch (Exception ex)
        {
            JwtTest = new JwtValidationOutput
            {
                Ok = false,
                Message = "Signature validation failed: " + ex.GetType().Name
            };
        }

        return Page();
    }

    private async Task LoadRealmsAsync()
    {
        var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
    }

    private static List<KeyPreview> BuildPreviews(string? jwksJson)
    {
        var list = new List<KeyPreview>();
        if (string.IsNullOrWhiteSpace(jwksJson)) return list;
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keys.EnumerateArray())
                {
                    list.Add(ParseKey(k));
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                list.Add(ParseKey(doc.RootElement));
            }
        }
        catch
        {
            // ignore parsing error here; validation happens elsewhere
        }
        return list;
    }

    private static string ComposeJwks(List<string> keysRaw)
        => "{\"keys\":[" + string.Join(",", keysRaw) + "]}";

    private static List<string> ExtractKeysRaw(string? jwksOrJwk)
    {
        var keys = new List<string>();
        if (string.IsNullOrWhiteSpace(jwksOrJwk)) return keys;
        try
        {
            using var doc = JsonDocument.Parse(jwksOrJwk);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keysArr.EnumerateArray()) keys.Add(k.GetRawText());
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                keys.Add(doc.RootElement.GetRawText());
            }
        }
        catch { }
        return keys;
    }

    private static KeyPreview ParseKey(JsonElement key)
    {
        string Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? string.Empty : string.Empty;
        var kty = Get(key, "kty");
        var kid = Get(key, "kid");
        var alg = Get(key, "alg");
        string details = string.Empty;
        if (string.Equals(kty, "RSA", StringComparison.OrdinalIgnoreCase))
        {
            var n = Get(key, "n");
            try
            {
                if (!string.IsNullOrEmpty(n))
                {
                    var nb = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(n);
                    details = $"modulus {nb.Length * 8} bits";
                }
            }
            catch { }
        }
        else if (string.Equals(kty, "EC", StringComparison.OrdinalIgnoreCase))
        {
            var crv = Get(key, "crv");
            details = string.IsNullOrEmpty(crv) ? "EC" : crv;
        }
        return new KeyPreview(kid, kty, alg, details);
    }

    public sealed record KeyPreview(string Kid, string Kty, string Alg, string Details);

    public sealed record JwtValidationOutput
    {
        public bool Ok { get; init; }
        public string? Message { get; init; }
        public string? HeaderAlg { get; init; }
        public string? HeaderKid { get; init; }
        public string? Iss { get; init; }
        public string? Sub { get; init; }
        public string? Aud { get; init; }
        public string? Iat { get; init; }
        public string? Nbf { get; init; }
        public string? Exp { get; init; }
    };

    public sealed class ClientInput
    {
        [Required, StringLength(200, MinimumLength = 2)]
        public string ClientId { get; set; } = string.Empty;
        [StringLength(200)]
        public string? ClientName { get; set; }
        [Required]
        public Guid RealmId { get; set; }
        public bool RequirePkce { get; set; } = true;
        public bool RequireConsent { get; set; } = true;
        [DataType(DataType.Password)]
        public string? ClientSecret { get; set; }
        [Display(Name = "Introspection audiences (comma-separated)")]
        public string? IntrospectionAudiences { get; set; }
        [Display(Name = "Public JWKS JSON")]
        public string? PublicJwksJson { get; set; }
        [Display(Name = "Public JWKS URI")]
        [Url]
        public string? PublicJwksUri { get; set; }
        [Display(Name = "Test signed JWT")]
        public string? TestJwt { get; set; }
    }
}
