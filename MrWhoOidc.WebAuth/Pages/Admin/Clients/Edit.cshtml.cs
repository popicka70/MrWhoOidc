using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MrWhoOidc.Auth.Services;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Crypto;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize]
public class EditModel(AuthDbContext db, IPasswordHasher hasher, ILogger<EditModel> logger) : PageModel
{
    private readonly ILogger<EditModel> _logger = logger;

    [FromRoute]
    public Guid Id { get; set; }

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    public List<KeyPreview> KeyPreviews { get; private set; } = new();

    public JwtValidationOutput? JwtTest { get; private set; }

    public string? GeneratedPrivateJwk { get; private set; }

    // Scopes tab model
    public List<Scope> AvailableScopes { get; private set; } = new();
    public List<string> AssignedScopes { get; private set; } = new();

    public List<ProviderRow> ProviderMappings { get; private set; } = new();

    [BindProperty]
    public string? NewScope { get; set; }

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    [BindProperty]
    public ProviderInputModel ProviderInput { get; set; } = new();

    public List<SelectListItem> ProviderOptions { get; private set; } = new();

    [BindProperty]
    public string? GenerateAlg { get; set; }

    [BindProperty]
    public string? RemoveKid { get; set; }

    public JwksValidationStatus? JwksStatus { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var client = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Id == Id);
        if (client is null) return NotFound();

        await LoadRealmsAsync();
        await LoadScopesAsync(client.Id);
        await LoadProviderMappingsAsync(client.Id);

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

        string? fields = null;
        if (!string.IsNullOrEmpty(client.IntrospectionResponseFieldsJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<string[]>(client.IntrospectionResponseFieldsJson) ?? Array.Empty<string>();
                fields = string.Join(", ", list);
            }
            catch { }
        }

        string? mtls = null;
        if (!string.IsNullOrEmpty(client.IntrospectionMtlsThumbprintsJson))
        {
            try
            {
                var list = JsonSerializer.Deserialize<string[]>(client.IntrospectionMtlsThumbprintsJson) ?? Array.Empty<string>();
                mtls = string.Join(", ", list);
            }
            catch { }
        }

        // Parse redirect allow-lists
        string? loginUris = null, logoutUris = null;
        if (!string.IsNullOrEmpty(client.AllowedLoginRedirectUrisJson))
        {
            try { loginUris = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.AllowedLoginRedirectUrisJson) ?? Array.Empty<string>()); } catch { }
        }
        if (!string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson))
        {
            try { logoutUris = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.AllowedLogoutRedirectUrisJson) ?? Array.Empty<string>()); } catch { }
        }

        // M2M fields
        string? m2mAudiences = null;
        if (!string.IsNullOrWhiteSpace(client.M2MAllowedAudiencesJson))
        {
            try { m2mAudiences = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.M2MAllowedAudiencesJson) ?? Array.Empty<string>()); } catch { }
        }
        string? m2mMtls = null;
        if (!string.IsNullOrWhiteSpace(client.M2MMtlsThumbprintsJson))
        {
            try { m2mMtls = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.M2MMtlsThumbprintsJson) ?? Array.Empty<string>()); } catch { }
        }

        Input = new ClientInput
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RealmId = client.RealmId,
            RequirePkce = client.RequirePkce,
            RequireConsent = client.RequireConsent,
            RequirePar = client.RequirePar,
            IntrospectionAudiences = introspectionAudiences,
            IntrospectionResponseFields = fields,
            IntrospectionMtlsThumbprints = mtls,
            PublicJwksJson = client.PublicJwksJson,
            PublicJwksUri = client.PublicJwksUri,
            AllowedLoginRedirectUris = loginUris,
            AllowedLogoutRedirectUris = logoutUris,
            AllowLocalLogin = client.AllowLocalLogin,
            AllowExternalIdp = client.AllowExternalIdp,
            AllowQrLogin = client.AllowQrLogin,
            LoginStyleKey = client.LoginStyleKey,
            // M2M
            M2MAllowedAudiences = m2mAudiences,
            M2MAccessTokenLifetimeSeconds = client.M2MAccessTokenLifetimeSeconds,
            AllowClientSecretBasic = client.AllowClientSecretBasic,
            AllowClientSecretPost = client.AllowClientSecretPost,
            AllowPrivateKeyJwt = client.AllowPrivateKeyJwt,
            M2MMtlsThumbprints = m2mMtls
        };

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);

        return Page();
    }

    public async Task<IActionResult> OnPostAddScopeAsync()
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        if (string.IsNullOrWhiteSpace(NewScope))
        {
            ModelState.AddModelError("NewScope", "Select a scope to add.");
            return Page();
        }
        var exists = await db.Scopes.AnyAsync(s => s.Name == NewScope);
        if (!exists)
        {
            ModelState.AddModelError("NewScope", "Unknown scope.");
            return Page();
        }
        var already = await db.ClientScopes.AnyAsync(cs => cs.ClientId == Id && cs.ScopeName == NewScope);
        if (!already)
        {
            db.ClientScopes.Add(new ClientScope { ClientId = Id, ScopeName = NewScope });
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRemoveScopeAsync(string scopeName)
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        var entity = await db.ClientScopes.FirstOrDefaultAsync(cs => cs.ClientId == Id && cs.ScopeName == scopeName);
        if (entity is not null)
        {
            db.ClientScopes.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostExtractPublicJwkAsync()
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        if (string.IsNullOrWhiteSpace(Input.PrivateJwk))
        {
            ModelState.AddModelError("Input.PrivateJwk", "Paste a private JWK or JWKS JSON.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        try
        {
            using var doc = JsonDocument.Parse(Input.PrivateJwk);
            var publicKeys = new List<object>();

            void AddPublicFromJwk(JsonWebKey jwk)
            {
                if (string.Equals(jwk.Kty, "RSA", StringComparison.OrdinalIgnoreCase))
                {
                    publicKeys.Add(new { kty = "RSA", kid = jwk.Kid, alg = jwk.Alg ?? "RS256", use = jwk.Use ?? "sig", n = jwk.N, e = jwk.E });
                }
                else if (string.Equals(jwk.Kty, "EC", StringComparison.OrdinalIgnoreCase))
                {
                    publicKeys.Add(new { kty = "EC", kid = jwk.Kid, alg = jwk.Alg ?? "ES256", use = jwk.Use ?? "sig", crv = jwk.Crv, x = jwk.X, y = jwk.Y });
                }
            }

            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                foreach (var k in keys.EnumerateArray())
                {
                    var jwk = new JsonWebKey(k.GetRawText());
                    AddPublicFromJwk(jwk);
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var jwk = new JsonWebKey(doc.RootElement.GetRawText());
                AddPublicFromJwk(jwk);
            }
            else
            {
                ModelState.AddModelError("Input.PrivateJwk", "Invalid JWK/JWKS JSON.");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                return Page();
            }

            var jwks = new { keys = publicKeys };
            Input.PublicJwksJson = JsonSerializer.Serialize(jwks, new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });

            // Force textarea to reflect new value instead of posted one
            ModelState.Remove("Input.PublicJwksJson");

            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        }
        catch
        {
            ModelState.AddModelError("Input.PrivateJwk", "Invalid JWK/JWKS JSON.");
        }

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
                D = null,
                P = null,
                Q = null,
                DP = null,
                DQ = null,
                QI = null
            };
            var jwks = new { keys = new[] { publicJwk } };
            Input.PublicJwksJson = JsonSerializer.Serialize(jwks, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        }

        // Reflect updated fields in UI
        ModelState.Remove("Input.PublicJwksJson");
        ModelState.Remove("GeneratedPrivateJwk");

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
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
                D = null,
                P = null,
                Q = null,
                DP = null,
                DQ = null,
                QI = null
            }.ToJson(includePrivate: false);
        }

        keysRaw.Insert(0, newPublicJwkJson); // prepend newest
        Input.PublicJwksJson = ComposeJwks(keysRaw);

        // Ensure textarea shows latest composed JWKS
        ModelState.Remove("Input.PublicJwksJson");
        ModelState.Remove("GeneratedPrivateJwk");

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostFetchJwksAsync()
    {
        await LoadRealmsAsync();
        if (string.IsNullOrWhiteSpace(Input.PublicJwksUri))
        {
            ModelState.AddModelError("Input.PublicJwksUri", "Enter a JWKS URI to fetch.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
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
                ModelState.Remove("Input.PublicJwksJson");
            }
        }
        catch
        {
            ModelState.AddModelError("Input.PublicJwksUri", "Failed to fetch JWKS from URI.");
        }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostValidateJwtAsync()
    {
        await LoadRealmsAsync();
        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        JwtTest = new JwtValidationOutput();

        if (string.IsNullOrWhiteSpace(Input.TestJwt))
        {
            ModelState.AddModelError("Input.TestJwt", "Paste a JWT to validate.");
            return Page();
        }

        var trimmed = Input.TestJwt.Trim();
        // Friendly hint when a JWK/JWKS is pasted instead of a JWT
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
        {
            try
            {
                using var _ = JsonDocument.Parse(trimmed);
                JwtTest = new JwtValidationOutput { Ok = false, Message = "Looks like a JWK/JWKS JSON. Paste a signed JWT here (not a key)." };
                return Page();
            }
            catch { /* fallthrough to normal parsing */ }
        }
        if (trimmed.Count(c => c == '.') < 2)
        {
            JwtTest = new JwtValidationOutput { Ok = false, Message = "Not a JWT. Expected three base64url segments separated by dots." };
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

    public async Task<IActionResult> OnPostSignTestJwtAsync()
    {
        await LoadRealmsAsync();
        if (string.IsNullOrWhiteSpace(Input.PrivateJwk))
        {
            ModelState.AddModelError("Input.PrivateJwk", "Paste a private JWK or JWKS JSON to sign a test JWT.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        try
        {
            // Resolve a JsonWebKey from either JWK or JWKS
            JsonWebKey? jwk = null;
            using (var doc = JsonDocument.Parse(Input.PrivateJwk))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() > 0)
                {
                    jwk = new JsonWebKey(keys[0].GetRawText());
                }
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    jwk = new JsonWebKey(doc.RootElement.GetRawText());
                }
            }

            if (jwk is null)
            {
                _logger.LogWarning("SignTestJwt: Invalid JWK/JWKS JSON for client {ClientId}", Input.ClientId);
                ModelState.AddModelError("Input.PrivateJwk", "Invalid JWK/JWKS JSON.");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                return Page();
            }

            var alg = !string.IsNullOrEmpty(jwk.Alg)
                ? jwk.Alg
                : string.Equals(jwk.Kty, "EC", StringComparison.OrdinalIgnoreCase)
                    ? SecurityAlgorithms.EcdsaSha256
                    : SecurityAlgorithms.RsaSha256;

            var creds = new SigningCredentials(jwk, alg);
            var header = new JwtHeader(creds);
            if (!string.IsNullOrEmpty(jwk.Kid)) header["kid"] = jwk.Kid;

            var now = DateTimeOffset.UtcNow;
            var payload = new JwtPayload
            {
                { "iss", Input.ClientId },
                { "sub", Input.ClientId },
                { "iat", now.ToUnixTimeSeconds() },
                { "exp", now.AddMinutes(5).ToUnixTimeSeconds() },
                { "jti", Guid.NewGuid().ToString("N") },
                { "name", Input.ClientName ?? Input.ClientId }
            };

            var token = new JwtSecurityToken(header, payload);
            Input.TestJwt = new JwtSecurityTokenHandler().WriteToken(token);
            ModelState.Remove("Input.TestJwt");
        }
        catch (Exception ex)
        {
            // Do not log the private key value; only log exception details
            _logger.LogError(ex, "SignTestJwt failed for client {ClientId}: {Error}", Input.ClientId, ex.Message);
            var reason = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";
            ModelState.AddModelError("Input.PrivateJwk", "Failed to sign test JWT: " + reason);
        }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveKeyAsync()
    {
        await LoadRealmsAsync();
        if (string.IsNullOrWhiteSpace(Input.PublicJwksJson) || string.IsNullOrWhiteSpace(RemoveKid))
        {
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }
        try
        {
            using var doc = JsonDocument.Parse(Input.PublicJwksJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            {
                var filtered = new List<string>();
                foreach (var k in keys.EnumerateArray())
                {
                    var kid = k.TryGetProperty("kid", out var kidEl) ? kidEl.GetString() : null;
                    if (!string.Equals(kid, RemoveKid, StringComparison.Ordinal))
                        filtered.Add(k.GetRawText());
                }
                Input.PublicJwksJson = ComposeJwks(filtered);
                ModelState.Remove("Input.PublicJwksJson");
            }
        }
        catch { }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync()
    {
        // Default Save handler
        if (!ModelState.IsValid)
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        // Validate JWKS JSON if provided and check kid uniqueness
        if (!string.IsNullOrWhiteSpace(Input.PublicJwksJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(Input.PublicJwksJson);
                var status = ComputeJwksStatus(Input.PublicJwksJson);
                if (status is { Ok: false })
                {
                    await LoadRealmsAsync();
                    await LoadScopesAsync(Id);
                    JwksStatus = status;
                    ModelState.AddModelError("Input.PublicJwksJson", status.Message ?? "Invalid JWKS");
                    KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                    return Page();
                }
            }
            catch
            {
                await LoadRealmsAsync();
                await LoadScopesAsync(Id);
                ModelState.AddModelError("Input.PublicJwksJson", "Invalid JSON.");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
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
                await LoadScopesAsync(Id);
                ModelState.AddModelError("Input.ClientId", "Client ID already exists");
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                return Page();
            }
        }

        client.ClientId = Input.ClientId;
        client.ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName;
        client.RealmId = Input.RealmId;
        client.RequirePkce = Input.RequirePkce;
        client.RequireConsent = Input.RequireConsent;
        client.RequirePar = Input.RequirePar;
        client.AllowLocalLogin = Input.AllowLocalLogin;
        client.AllowExternalIdp = Input.AllowExternalIdp;
        client.AllowQrLogin = Input.AllowQrLogin;
        client.LoginStyleKey = string.IsNullOrWhiteSpace(Input.LoginStyleKey) ? null : Input.LoginStyleKey.Trim();
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

        // Introspection response fields allow-list
        if (!string.IsNullOrWhiteSpace(Input.IntrospectionResponseFields))
        {
            var list = Input.IntrospectionResponseFields
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.IntrospectionResponseFieldsJson = JsonSerializer.Serialize(list);
        }
        else
        {
            client.IntrospectionResponseFieldsJson = null;
        }

        // Introspection mTLS thumbprints
        if (!string.IsNullOrWhiteSpace(Input.IntrospectionMtlsThumbprints))
        {
            var list = Input.IntrospectionMtlsThumbprints
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            client.IntrospectionMtlsThumbprintsJson = JsonSerializer.Serialize(list);
        }
        else
        {
            client.IntrospectionMtlsThumbprintsJson = null;
        }

        // Persist JWKS/JWKS URI
        client.PublicJwksJson = string.IsNullOrWhiteSpace(Input.PublicJwksJson) ? null : Input.PublicJwksJson;
        client.PublicJwksUri = string.IsNullOrWhiteSpace(Input.PublicJwksUri) ? null : Input.PublicJwksUri;

        // Persist redirect allow-lists
        client.AllowedLoginRedirectUrisJson = NormalizeUrlsToJson(Input.AllowedLoginRedirectUris);
        client.AllowedLogoutRedirectUrisJson = NormalizeUrlsToJson(Input.AllowedLogoutRedirectUris);

        // M2M: allowed audiences
        if (!string.IsNullOrWhiteSpace(Input.M2MAllowedAudiences))
        {
            var list = Input.M2MAllowedAudiences
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.M2MAllowedAudiencesJson = JsonSerializer.Serialize(list);
        }
        else
        {
            client.M2MAllowedAudiencesJson = null;
        }

        // M2M lifetime override
        client.M2MAccessTokenLifetimeSeconds = Input.M2MAccessTokenLifetimeSeconds.HasValue && Input.M2MAccessTokenLifetimeSeconds.Value > 0
            ? Input.M2MAccessTokenLifetimeSeconds
            : null;

        // Token endpoint auth method toggles
        client.AllowClientSecretBasic = Input.AllowClientSecretBasic;
        client.AllowClientSecretPost = Input.AllowClientSecretPost;
        client.AllowPrivateKeyJwt = Input.AllowPrivateKeyJwt;

        // M2M mTLS thumbprints
        if (!string.IsNullOrWhiteSpace(Input.M2MMtlsThumbprints))
        {
            var list = Input.M2MMtlsThumbprints
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            client.M2MMtlsThumbprintsJson = JsonSerializer.Serialize(list);
        }
        else
        {
            client.M2MMtlsThumbprintsJson = null;
        }

        await db.SaveChangesAsync();
        return RedirectToPage("Index");
    }

    private static string? NormalizeUrlsToJson(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .Select(s => s.Trim())
            .ToArray();
        return list.Length == 0 ? null : JsonSerializer.Serialize(list);
    }

    private async Task LoadRealmsAsync()
    {
        var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Name).ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
    }

    private async Task LoadScopesAsync(Guid clientId)
    {
        AvailableScopes = await db.Scopes.AsNoTracking().OrderBy(s => s.Name).ToListAsync();
        AssignedScopes = await db.ClientScopes.AsNoTracking().Where(cs => cs.ClientId == clientId).Select(cs => cs.ScopeName).OrderBy(n => n).ToListAsync();
        // Filter available list to those not yet assigned
        AvailableScopes = AvailableScopes.Where(s => !AssignedScopes.Contains(s.Name, StringComparer.Ordinal)).ToList();
    }

    private async Task LoadProviderMappingsAsync(Guid clientId)
    {
        ProviderOptions = await db.IdentityProviders.AsNoTracking()
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new SelectListItem(p.DisplayName ?? p.Name, p.Id.ToString()))
            .ToListAsync();

        ProviderMappings = await db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.ClientId == clientId)
            .Join(db.IdentityProviders.AsNoTracking(), m => m.IdentityProviderId, p => p.Id, (m, p) => new ProviderRow
            {
                IdentityProviderId = p.Id,
                ProviderName = p.Name,
                ProviderDisplay = p.DisplayName ?? p.Name,
                Enabled = m.Enabled,
                IsDefaultForClient = m.IsDefaultForClient,
                AutoRedirectIfSingle = m.AutoRedirectIfSingle,
                RequiredAcr = m.RequiredAcr,
                Order = m.Order
            })
            .OrderBy(r => r.Order)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostAddProviderAsync()
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        await LoadProviderMappingsAsync(Id);
        if (!ModelState.IsValid)
            return Page();

        if (ProviderInput.IdentityProviderId == Guid.Empty)
        {
            ModelState.AddModelError("ProviderInput.IdentityProviderId", "Select a provider.");
            return Page();
        }

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == ProviderInput.IdentityProviderId);
        if (entity is null)
        {
            entity = new ClientIdentityProvider { ClientId = Id, IdentityProviderId = ProviderInput.IdentityProviderId };
            db.ClientIdentityProviders.Add(entity);
        }
        entity.Enabled = ProviderInput.Enabled;
        entity.IsDefaultForClient = ProviderInput.IsDefaultForClient;
        entity.AutoRedirectIfSingle = ProviderInput.AutoRedirectIfSingle;
        entity.RequiredAcr = string.IsNullOrWhiteSpace(ProviderInput.RequiredAcr) ? null : ProviderInput.RequiredAcr.Trim();
        entity.Order = ProviderInput.Order;

        await db.SaveChangesAsync();
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteProviderAsync(Guid providerId)
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        await LoadProviderMappingsAsync(Id);

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return RedirectToPage(new { id = Id });
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

    public sealed record JwksValidationStatus(bool Ok, string Summary, string? Message, int KeyCount, int UniqueKidCount, List<string> DuplicateKids);

    private static JwksValidationStatus? ComputeJwksStatus(string? jwksJson)
    {
        if (string.IsNullOrWhiteSpace(jwksJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(jwksJson);
            var keys = new List<JsonElement>();
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("keys", out var keysArr) && keysArr.ValueKind == JsonValueKind.Array)
            {
                keys = keysArr.EnumerateArray().ToList();
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                keys.Add(doc.RootElement);
            }
            else
            {
                return new JwksValidationStatus(false, "Invalid", "JWKS must be an object with 'keys' array or a single JWK object.", 0, 0, []);
            }

            var count = keys.Count;
            var kids = keys.Select(k => k.TryGetProperty("kid", out var kid) ? kid.GetString() : null).ToList();
            var nonNullKids = kids.Where(k => !string.IsNullOrWhiteSpace(k)).ToList();
            var dup = nonNullKids
                .GroupBy(k => k, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key!)
                .ToList();

            var ok = dup.Count == 0;
            var summary = ok ? "Valid JWKS" : "Duplicates";
            var msg = ok ? $"{count} key(s), {nonNullKids.Distinct(StringComparer.Ordinal).Count()} distinct kid" : $"Duplicate kid(s): {string.Join(", ", dup)}";
            return new JwksValidationStatus(ok, summary, msg, count, nonNullKids.Distinct(StringComparer.Ordinal).Count(), dup);
        }
        catch (Exception ex)
        {
            return new JwksValidationStatus(false, "Invalid", ex.Message, 0, 0, []);
        }
    }

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
        public bool RequirePar { get; set; } = false;
        [DataType(DataType.Password)]
        public string? ClientSecret { get; set; }
        [Display(Name = "Introspection audiences (comma-separated)")]
        public string? IntrospectionAudiences { get; set; }
        [Display(Name = "Introspection response fields (comma-separated)")]
        public string? IntrospectionResponseFields { get; set; }
        [Display(Name = "Introspection mTLS thumbprints (comma-separated)")]
        public string? IntrospectionMtlsThumbprints { get; set; }
        [Display(Name = "Public JWKS JSON")]
        public string? PublicJwksJson { get; set; }
        [Display(Name = "Public JWKS URI")]
        [Url]
        public string? PublicJwksUri { get; set; }
        [Display(Name = "Test signed JWT")]
        public string? TestJwt { get; set; }
        [Display(Name = "Private JWK or JWKS (one-time)")]
        public string? PrivateJwk { get; set; }

        [Display(Name = "Allowed login redirect URIs (comma-separated)")]
        public string? AllowedLoginRedirectUris { get; set; }
        [Display(Name = "Allowed logout redirect URIs (comma-separated)")]
        public string? AllowedLogoutRedirectUris { get; set; }

        // New: login method toggles
        [Display(Name = "Allow local username/password login")]
        public bool AllowLocalLogin { get; set; } = true;
        [Display(Name = "Allow external identity providers")]
        public bool AllowExternalIdp { get; set; } = true;
        [Display(Name = "Allow QR code login")]
        public bool AllowQrLogin { get; set; } = false;

        // New: login UI style scheme
        [StringLength(50)]
        public string? LoginStyleKey { get; set; }

        // New: M2M policy fields
        [Display(Name = "M2M allowed audiences (comma-separated)")]
        public string? M2MAllowedAudiences { get; set; }
        [Display(Name = "M2M access token lifetime (seconds)")]
        [Range(0, 86400)]
        public int? M2MAccessTokenLifetimeSeconds { get; set; }
        [Display(Name = "Allow client_secret_basic")]
        public bool AllowClientSecretBasic { get; set; } = true;
        [Display(Name = "Allow client_secret_post")]
        public bool AllowClientSecretPost { get; set; } = true;
        [Display(Name = "Allow private_key_jwt")]
        public bool AllowPrivateKeyJwt { get; set; } = true;
        [Display(Name = "M2M mTLS thumbprints (comma-separated)")]
        public string? M2MMtlsThumbprints { get; set; }
    }

    public sealed class ProviderRow
    {
        public Guid IdentityProviderId { get; set; }
        public string ProviderName { get; set; } = string.Empty;
        public string ProviderDisplay { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public bool IsDefaultForClient { get; set; }
        public bool AutoRedirectIfSingle { get; set; }
        public string? RequiredAcr { get; set; }
        public int Order { get; set; }
    }

    public sealed class ProviderInputModel
    {
        [Required]
        public Guid IdentityProviderId { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsDefaultForClient { get; set; } = false;
        public bool AutoRedirectIfSingle { get; set; } = false;
        [StringLength(100)]
        public string? RequiredAcr { get; set; }
        public int Order { get; set; } = 0;
    }
}
