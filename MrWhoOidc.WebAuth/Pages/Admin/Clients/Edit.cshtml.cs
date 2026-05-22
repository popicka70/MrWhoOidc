using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Options;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.Auth.Protocols;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Filters;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize(Policy = "tenant-admin")]
public class EditModel(
    AuthDbContext db,
    IPasswordHasher hasher,
    ILogger<EditModel> logger,
    MrWhoOidc.WebAuth.Observability.IAuditSink audit,
    IOptions<OidcOptions> oidcOptions,
    ITenantAccessor tenantAccessor,
    IClientStore clientStore,
    IScopeResolver scopeResolver,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private readonly ILogger<EditModel> _logger = logger;
    private readonly MrWhoOidc.WebAuth.Observability.IAuditSink _audit = audit;

    [FromRoute]
    public Guid Id { get; set; }

    [FromQuery(Name = "tab")]
    public string? ActiveTab { get; set; }

    public List<SelectListItem> RealmOptions { get; private set; } = new();

    public List<KeyPreview> KeyPreviews { get; private set; } = new();

    public JwtValidationOutput? JwtTest { get; private set; }

    public string? GeneratedPrivateJwk { get; private set; }

    public List<ClientSecretViewModel> ClientSecrets { get; private set; } = new();

    public string ClientDisplayName { get; private set; } = string.Empty;

    public string ClientPublicId { get; private set; } = string.Empty;

    public bool HasLegacyClientSecretHash { get; private set; }

    public string ActiveSigningAlg { get; private set; } = SecurityConstants.JwtAlgorithms.RS256;

    public override async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (tenantId.HasValue)
        {
            ActiveSigningAlg = await GetActiveTenantSigningAlgorithmAsync(tenantId.Value);
        }

        await next();
    }

    // Scopes tab model
    public List<Scope> AvailableScopes { get; private set; } = new();
    public List<Scope> GlobalAvailableScopes { get; private set; } = new();
    public List<Scope> TenantAvailableScopes { get; private set; } = new();
    public List<string> AssignedScopes { get; private set; } = new();
    public List<string> GlobalAssignedScopes { get; private set; } = new();
    public List<string> TenantAssignedScopes { get; private set; } = new();
    public string CurrentTenantSlug => TenantAccessor.CurrentTenant?.Slug ?? "tenant";

    // Users tab model
    public List<UserAssignmentViewModel> AssignedUsers { get; private set; } = new();
    public List<UserAssignmentViewModel> AvailableUsers { get; private set; } = new();

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

    [BindProperty]
    public SecretInputModel SecretInput { get; set; } = new();

    public JwksValidationStatus? JwksStatus { get; private set; }

    // IdP Chaining — tenant-aware issuer URL (the single URL an upstream IdP needs)
    public string IdpChainingIssuerUrl { get; private set; } = string.Empty;

    [TempData]
    public string? SecretErrorMessage { get; set; }

    [TempData]
    public string? SecretSuccessMessage { get; set; }

    [TempData]
    public string? SecretNewSecretValue { get; set; }

    // Manual TempData handling to avoid Guid-to-String casting issues
    // (ASP.NET deserializes GUID-like strings as System.Guid objects)
    public string? SecretNewSecretIdentifier { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == Id && c.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
        if (client is null) return NotFound();
        if (client.IsSystemClient)
        {
            TempData["Error"] = "System clients are managed automatically from tenant settings.";
            return TenantAwareRedirect("/Admin/Clients");
        }

        // Manual TempData handling for SecretNewSecretIdentifier to avoid Guid-to-String cast issues
        if (TempData.ContainsKey("SecretNewSecretIdentifier"))
        {
            SecretNewSecretIdentifier = TempData["SecretNewSecretIdentifier"]?.ToString();
        }
        // Cleanup old malformed TempData entries (if any)
        if (TempData.ContainsKey("SecretNewSecretId"))
        {
            TempData.Remove("SecretNewSecretId");
        }

        await LoadRealmsAsync();
        await LoadScopesAsync(client.Id);
        await LoadProviderMappingsAsync(client.Id);
        await LoadClientSecretsAsync(client.Id);
        await LoadUserAssignmentsAsync(client.Id, client.RealmId, currentTenantId.Value);


        // Tenant-aware issuer URL — what an upstream IdP needs to chain into this instance
        IdpChainingIssuerUrl = HttpContext.GetIssuer(oidcOptions.Value).TrimEnd('/');

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
            try { loginUris = string.Join("\n", JsonSerializer.Deserialize<string[]>(client.AllowedLoginRedirectUrisJson) ?? Array.Empty<string>()); } catch { }
        }
        if (!string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson))
        {
            try { logoutUris = string.Join("\n", JsonSerializer.Deserialize<string[]>(client.AllowedLogoutRedirectUrisJson) ?? Array.Empty<string>()); } catch { }
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

        // OBO fields parse
        string? oboCallers = null;
        if (!string.IsNullOrWhiteSpace(client.OboAllowedCallersJson))
        {
            try { oboCallers = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.OboAllowedCallersJson) ?? Array.Empty<string>()); } catch { }
        }
        string? oboSourceAud = null;
        if (!string.IsNullOrWhiteSpace(client.OboAllowedSourceAudiencesJson))
        {
            try { oboSourceAud = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.OboAllowedSourceAudiencesJson) ?? Array.Empty<string>()); } catch { }
        }
        string? oboTargetAud = null;
        if (!string.IsNullOrWhiteSpace(client.OboAllowedTargetAudiencesJson))
        {
            try { oboTargetAud = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.OboAllowedTargetAudiencesJson) ?? Array.Empty<string>()); } catch { }
        }
        string? oboScopes = null;
        if (!string.IsNullOrWhiteSpace(client.OboAllowedScopesJson))
        {
            try { oboScopes = string.Join(", ", JsonSerializer.Deserialize<string[]>(client.OboAllowedScopesJson) ?? Array.Empty<string>()); } catch { }
        }

        Input = new ClientInput
        {
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RealmId = client.RealmId,
            RequirePkce = client.RequirePkce,
            RequireConsent = client.RequireConsent,
            RequirePar = client.RequirePar,
            SubjectType = client.SubjectType,
            SectorIdentifierUri = client.SectorIdentifierUri,
            IntrospectionAudiences = introspectionAudiences,
            IntrospectionResponseFields = fields,
            IntrospectionMtlsThumbprints = mtls,
            PublicJwksJson = client.PublicJwksJson,
            PublicJwksUri = client.PublicJwksUri,
            IdTokenSignedResponseAlg = client.IdTokenSignedResponseAlg,
            IdTokenEncryptedResponseAlg = client.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = client.IdTokenEncryptedResponseEnc,
            UserInfoSignedResponseAlg = client.UserInfoSignedResponseAlg,
            UserInfoEncryptedResponseAlg = client.UserInfoEncryptedResponseAlg,
            UserInfoEncryptedResponseEnc = client.UserInfoEncryptedResponseEnc,
            AuthorizationSignedResponseAlg = client.AuthorizationSignedResponseAlg,
            AuthorizationEncryptedResponseAlg = client.AuthorizationEncryptedResponseAlg,
            AuthorizationEncryptedResponseEnc = client.AuthorizationEncryptedResponseEnc,
            AllowedLoginRedirectUris = loginUris,
            AllowedLogoutRedirectUris = logoutUris,
            AllowLocalLogin = client.AllowLocalLogin,
            AllowExternalIdp = client.AllowExternalIdp,
            AllowQrLogin = client.AllowQrLogin,
            LoginStyleKey = client.LoginStyleKey,
            AutoApprovalMode = client.AutoApprovalMode,
            AutoAssignNewUsersToClient = client.AutoAssignNewUsersToClient,
            // M2M
            M2MAllowedAudiences = m2mAudiences,
            M2MAccessTokenLifetimeSeconds = client.M2MAccessTokenLifetimeSeconds,
            AllowClientSecretBasic = client.AllowClientSecretBasic,
            AllowClientSecretPost = client.AllowClientSecretPost,
            AllowPrivateKeyJwt = client.AllowPrivateKeyJwt,
            M2MMtlsThumbprints = m2mMtls,
            // OBO
            OboEnabled = client.OboEnabled != false, // null or true => enabled
            OboAllowedCallers = oboCallers,
            OboAllowedSourceAudiences = oboSourceAud,
            OboAllowedTargetAudiences = oboTargetAud,
            OboAllowedScopes = oboScopes,
            OboMaxDelegationDepth = client.OboMaxDelegationDepth,
            OboMaxLifetimeMinutes = client.OboMaxLifetimeMinutes,
            OboDpopMode = client.OboDpopMode ?? OboDpopMode.Deny
        };

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);

        ClientDisplayName = client.ClientName ?? client.ClientId;
        ClientPublicId = client.ClientId;
#pragma warning disable CS0618
        HasLegacyClientSecretHash = !string.IsNullOrEmpty(client.ClientSecretHash);
#pragma warning restore CS0618

        if (SecretInput is null)
        {
            SecretInput = new SecretInputModel();
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAddScopeAsync()
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=scopes");
    }

    public async Task<IActionResult> OnPostRemoveScopeAsync(string scopeName)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        var entity = await db.ClientScopes.FirstOrDefaultAsync(cs => cs.ClientId == Id && cs.ScopeName == scopeName);
        if (entity is not null)
        {
            db.ClientScopes.Remove(entity);
            await db.SaveChangesAsync();
        }
        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=scopes");
    }

    public async Task<IActionResult> OnPostAssignUserAsync(Guid userId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        // Load client to get RealmId
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == Id && c.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
        if (client is null)
        {
            return NotFound();
        }

        // Verify user belongs to same tenant (for multi-tenant mode)
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
        if (user is null)
        {
            return NotFound();
        }

        // Check if assignment already exists
        var existingAssignment = await db.UserClientAssignments
            .Where(a => a.UserId == userId && a.ClientId == Id && a.RealmId == client.RealmId)
            .FirstOrDefaultAsync();

        if (existingAssignment is null)
        {
            db.UserClientAssignments.Add(new UserClientAssignment
            {
                UserId = userId,
                ClientId = Id,
                RealmId = client.RealmId,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=users");
    }

    public async Task<IActionResult> OnPostUnassignUserAsync(Guid userId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        // Load client to get RealmId
        var client = await db.Clients.AsNoTracking()
            .Where(c => c.Id == Id && c.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
        if (client is null)
        {
            return NotFound();
        }

        // Remove the assignment
        var assignment = await db.UserClientAssignments
            .Where(a => a.UserId == userId && a.ClientId == Id && a.RealmId == client.RealmId)
            .FirstOrDefaultAsync();

        if (assignment is not null)
        {
            db.UserClientAssignments.Remove(assignment);
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=users");
    }

    public async Task<IActionResult> OnPostExtractPublicJwkAsync()
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
            using var http = MrWhoOidc.Auth.Utils.NetworkSecurity.CreateSafeHttpClient(TimeSpan.FromSeconds(10));
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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
                Sub = principal.FindFirst(OidcConstants.Claims.Subject)?.Value,
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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
                { OidcConstants.Claims.Subject, Input.ClientId },
                { "iat", now.ToUnixTimeSeconds() },
                { "exp", now.AddMinutes(5).ToUnixTimeSeconds() },
                { "jti", Guid.NewGuid().ToString("N") },
                { OidcConstants.Claims.Name, Input.ClientName ?? Input.ClientId }
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
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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

        // Validate subject identifier settings
        var subjectType = (Input.SubjectType ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(subjectType))
        {
            subjectType = OidcConstants.SubjectTypes.Public;
        }

        var isPublic = string.Equals(subjectType, OidcConstants.SubjectTypes.Public, StringComparison.OrdinalIgnoreCase);
        var isPairwise = string.Equals(subjectType, OidcConstants.SubjectTypes.Pairwise, StringComparison.OrdinalIgnoreCase);
        if (!isPublic && !isPairwise)
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            ModelState.AddModelError("Input.SubjectType", "Subject type must be 'public' or 'pairwise'.");
            return Page();
        }

        // Canonicalize
        Input.SubjectType = isPairwise ? OidcConstants.SubjectTypes.Pairwise : OidcConstants.SubjectTypes.Public;

        if (isPairwise && !string.IsNullOrWhiteSpace(Input.SectorIdentifierUri))
        {
            var sectorIdentifierUri = Input.SectorIdentifierUri.Trim();
            if (!Uri.TryCreate(sectorIdentifierUri, UriKind.Absolute, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host))
            {
                await LoadRealmsAsync();
                await LoadScopesAsync(Id);
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                ModelState.AddModelError("Input.SectorIdentifierUri", "Sector identifier URI must be an absolute URI with a host.");
                return Page();
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                await LoadRealmsAsync();
                await LoadScopesAsync(Id);
                KeyPreviews = BuildPreviews(Input.PublicJwksJson);
                JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
                ModelState.AddModelError("Input.SectorIdentifierUri", "Sector identifier URI must use HTTPS.");
                return Page();
            }

            Input.SectorIdentifierUri = sectorIdentifierUri;
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

        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        // --- OIDC response crypto settings ---
        Input.IdTokenSignedResponseAlg = string.IsNullOrWhiteSpace(Input.IdTokenSignedResponseAlg) ? null : Input.IdTokenSignedResponseAlg.Trim();
        Input.IdTokenEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseAlg) ? null : Input.IdTokenEncryptedResponseAlg.Trim();
        Input.IdTokenEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseEnc) ? null : Input.IdTokenEncryptedResponseEnc.Trim();
        Input.UserInfoSignedResponseAlg = string.IsNullOrWhiteSpace(Input.UserInfoSignedResponseAlg) ? null : Input.UserInfoSignedResponseAlg.Trim();
        Input.UserInfoEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseAlg) ? null : Input.UserInfoEncryptedResponseAlg.Trim();
        Input.UserInfoEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseEnc) ? null : Input.UserInfoEncryptedResponseEnc.Trim();
        Input.AuthorizationSignedResponseAlg = string.IsNullOrWhiteSpace(Input.AuthorizationSignedResponseAlg) ? null : Input.AuthorizationSignedResponseAlg.Trim();
        Input.AuthorizationEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseAlg) ? null : Input.AuthorizationEncryptedResponseAlg.Trim();
        Input.AuthorizationEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseEnc) ? null : Input.AuthorizationEncryptedResponseEnc.Trim();

        if (!string.IsNullOrWhiteSpace(Input.IdTokenSignedResponseAlg) && string.Equals(Input.IdTokenSignedResponseAlg, SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            ModelState.AddModelError("Input.IdTokenSignedResponseAlg", "'none' is not supported.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(Input.IdTokenSignedResponseAlg) && !string.Equals(Input.IdTokenSignedResponseAlg, ActiveSigningAlg, StringComparison.Ordinal))
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            ModelState.AddModelError("Input.IdTokenSignedResponseAlg", $"Must match tenant active signing alg '{ActiveSigningAlg}' or be empty.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(Input.UserInfoSignedResponseAlg) && !string.Equals(Input.UserInfoSignedResponseAlg, ActiveSigningAlg, StringComparison.Ordinal))
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            ModelState.AddModelError("Input.UserInfoSignedResponseAlg", $"Must match tenant active signing alg '{ActiveSigningAlg}' or be empty.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        if (!string.IsNullOrWhiteSpace(Input.AuthorizationSignedResponseAlg) && !string.Equals(Input.AuthorizationSignedResponseAlg, ActiveSigningAlg, StringComparison.Ordinal))
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            ModelState.AddModelError("Input.AuthorizationSignedResponseAlg", $"Must match tenant active signing alg '{ActiveSigningAlg}' or be empty.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        ValidateEncryptionPairOrError(
            Input.IdTokenEncryptedResponseAlg,
            Input.IdTokenEncryptedResponseEnc,
            "Input.IdTokenEncryptedResponseAlg",
            "Input.IdTokenEncryptedResponseEnc");

        ValidateEncryptionPairOrError(
            Input.UserInfoEncryptedResponseAlg,
            Input.UserInfoEncryptedResponseEnc,
            "Input.UserInfoEncryptedResponseAlg",
            "Input.UserInfoEncryptedResponseEnc");

        ValidateEncryptionPairOrError(
            Input.AuthorizationEncryptedResponseAlg,
            Input.AuthorizationEncryptedResponseEnc,
            "Input.AuthorizationEncryptedResponseAlg",
            "Input.AuthorizationEncryptedResponseEnc");

        if (!ModelState.IsValid)
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        // If enabling encryption, require some form of JWKS
        if ((Input.IdTokenEncryptedResponseAlg is not null || Input.IdTokenEncryptedResponseEnc is not null
            || Input.UserInfoEncryptedResponseAlg is not null || Input.UserInfoEncryptedResponseEnc is not null
            || Input.AuthorizationEncryptedResponseAlg is not null || Input.AuthorizationEncryptedResponseEnc is not null)
            && string.IsNullOrWhiteSpace(Input.PublicJwksJson)
            && string.IsNullOrWhiteSpace(Input.PublicJwksUri))
        {
            await LoadRealmsAsync();
            await LoadScopesAsync(Id);
            ModelState.AddModelError("Input.PublicJwksJson", "Provide a public JWKS (JSON or URI) to enable encrypted responses.");
            KeyPreviews = BuildPreviews(Input.PublicJwksJson);
            JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
            return Page();
        }

        var client = await db.Clients.FirstOrDefaultAsync(c => c.Id == Id && c.TenantId == currentTenantId.Value);
        if (client is null) return NotFound();
        // Capture old values for audit comparison
        var oldBclUri = client.BackChannelLogoutUri;
        var oldBclSess = client.BackChannelLogoutSessionRequired;

        // If client id changed, enforce uniqueness within tenant
        if (!string.Equals(client.ClientId, Input.ClientId, StringComparison.Ordinal))
        {
            var exists = await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId && c.TenantId == client.TenantId);
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
        client.AutoApprovalMode = Input.AutoApprovalMode;
        client.AutoAssignNewUsersToClient = Input.AutoAssignNewUsersToClient;
        if (!string.IsNullOrEmpty(Input.ClientSecret))
        {
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            client.ClientSecretHash = hasher.Hash(Input.ClientSecret);
#pragma warning restore CS0618
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

        // Persist OIDC crypto response metadata
        client.IdTokenSignedResponseAlg = Input.IdTokenSignedResponseAlg;
        client.IdTokenEncryptedResponseAlg = Input.IdTokenEncryptedResponseAlg;
        client.IdTokenEncryptedResponseEnc = Input.IdTokenEncryptedResponseEnc;
        client.UserInfoSignedResponseAlg = Input.UserInfoSignedResponseAlg;
        client.UserInfoEncryptedResponseAlg = Input.UserInfoEncryptedResponseAlg;
        client.UserInfoEncryptedResponseEnc = Input.UserInfoEncryptedResponseEnc;
        client.AuthorizationSignedResponseAlg = Input.AuthorizationSignedResponseAlg;
        client.AuthorizationEncryptedResponseAlg = Input.AuthorizationEncryptedResponseAlg;
        client.AuthorizationEncryptedResponseEnc = Input.AuthorizationEncryptedResponseEnc;

        // Persist redirect allow-lists
        client.AllowedLoginRedirectUrisJson = NormalizeUrlsToJson(Input.AllowedLoginRedirectUris);
        client.AllowedLogoutRedirectUrisJson = NormalizeUrlsToJson(Input.AllowedLogoutRedirectUris);

        // Persist OIDC subject identifier settings
        client.SubjectType = Input.SubjectType;
        client.SectorIdentifierUri = string.IsNullOrWhiteSpace(Input.SectorIdentifierUri) ? null : Input.SectorIdentifierUri.Trim();

        // Back-channel logout fields with validation
        if (!string.IsNullOrWhiteSpace(Input.BackChannelLogoutUri))
        {
            var uri = Input.BackChannelLogoutUri.Trim();
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
            {
                ModelState.AddModelError("Input.BackChannelLogoutUri", "Must be an absolute URI.");
                return Page();
            }
            var isHttps = string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var allowHttpDev = HttpContext.RequestServices.GetService<IConfiguration>()?
                .GetValue<bool>("Dev:AllowHttpBackchannel") == true;
            if (!isHttps && !allowHttpDev)
            {
                ModelState.AddModelError("Input.BackChannelLogoutUri", "HTTPS is required in production. Set Dev:AllowHttpBackchannel=true to allow http for local/dev.");
                return Page();
            }
            client.BackChannelLogoutUri = parsed.ToString().TrimEnd('/');
        }
        else
        {
            client.BackChannelLogoutUri = null;
        }
        client.BackChannelLogoutSessionRequired = Input.BackChannelLogoutSessionRequired;

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

        // --- OBO policy fields ---
        client.OboEnabled = Input.OboEnabled;

        // Allowed callers
        if (!string.IsNullOrWhiteSpace(Input.OboAllowedCallers))
        {
            var list = Input.OboAllowedCallers
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.OboAllowedCallersJson = list.Length == 0 ? null : JsonSerializer.Serialize(list);
        }
        else
        {
            client.OboAllowedCallersJson = null;
        }

        // Allowed source audiences
        if (!string.IsNullOrWhiteSpace(Input.OboAllowedSourceAudiences))
        {
            var list = Input.OboAllowedSourceAudiences
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.OboAllowedSourceAudiencesJson = list.Length == 0 ? null : JsonSerializer.Serialize(list);
        }
        else
        {
            client.OboAllowedSourceAudiencesJson = null;
        }

        // Allowed target audiences
        if (!string.IsNullOrWhiteSpace(Input.OboAllowedTargetAudiences))
        {
            var list = Input.OboAllowedTargetAudiences
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.OboAllowedTargetAudiencesJson = list.Length == 0 ? null : JsonSerializer.Serialize(list);
        }
        else
        {
            client.OboAllowedTargetAudiencesJson = null;
        }

        // Allowed scopes
        if (!string.IsNullOrWhiteSpace(Input.OboAllowedScopes))
        {
            var list = Input.OboAllowedScopes
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            client.OboAllowedScopesJson = list.Length == 0 ? null : JsonSerializer.Serialize(list);
        }
        else
        {
            client.OboAllowedScopesJson = null;
        }

        // Max depth / lifetime
        client.OboMaxDelegationDepth = Input.OboMaxDelegationDepth.HasValue && Input.OboMaxDelegationDepth.Value > 0
            ? Input.OboMaxDelegationDepth
            : null;
        client.OboMaxLifetimeMinutes = Input.OboMaxLifetimeMinutes.HasValue && Input.OboMaxLifetimeMinutes.Value > 0
            ? Input.OboMaxLifetimeMinutes
            : null;

        client.OboDpopMode = Input.OboDpopMode;

        await db.SaveChangesAsync();

        // Invalidate client cache after update
        await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);

        // Audit backchannel field changes if any
        if (!string.Equals(oldBclUri, client.BackChannelLogoutUri, StringComparison.Ordinal) || oldBclSess != client.BackChannelLogoutSessionRequired)
        {
            _audit.Emit("admin.client.backchannel.update", new
            {
                client_id = client.ClientId,
                backchannel_logout_uri_old = oldBclUri,
                backchannel_logout_uri_new = client.BackChannelLogoutUri,
                backchannel_logout_session_required_old = oldBclSess,
                backchannel_logout_session_required_new = client.BackChannelLogoutSessionRequired,
                user = User?.Identity?.Name,
                ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                when = DateTimeOffset.UtcNow
            });
        }
        return TenantAwareRedirect("/Admin/Clients");
    }

    private void ValidateEncryptionPairOrError(string? alg, string? enc, string algKey, string encKey)
    {
        // Only supported: RSA-OAEP + A256CBC-HS512. Either both empty (disabled) or both set.
        var algSet = !string.IsNullOrWhiteSpace(alg);
        var encSet = !string.IsNullOrWhiteSpace(enc);
        if (!algSet && !encSet)
        {
            return;
        }

        if (!algSet)
        {
            ModelState.AddModelError(algKey, "Select an encryption alg or clear the enc value.");
        }
        if (!encSet)
        {
            ModelState.AddModelError(encKey, "Select an encryption enc or clear the alg value.");
        }
        if (!algSet || !encSet)
        {
            return;
        }

        if (!string.Equals(alg, SecurityAlgorithms.RsaOAEP, StringComparison.Ordinal))
        {
            ModelState.AddModelError(algKey, $"Unsupported alg. Supported: '{SecurityAlgorithms.RsaOAEP}'.");
        }
        if (!string.Equals(enc, SecurityAlgorithms.Aes256CbcHmacSha512, StringComparison.Ordinal))
        {
            ModelState.AddModelError(encKey, $"Unsupported enc. Supported: '{SecurityAlgorithms.Aes256CbcHmacSha512}'.");
        }
    }

    private async Task<string> GetActiveTenantSigningAlgorithmAsync(Guid tenantId)
    {
        var alg = await db.SigningKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Alg)
            .FirstOrDefaultAsync();

        return string.IsNullOrWhiteSpace(alg) ? SecurityConstants.JwtAlgorithms.RS256 : alg;
    }

    private static string? NormalizeUrlsToJson(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // Split by newlines first (for textarea input), then fallback to comma-separated for backward compatibility
        var list = input
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => line.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .SelectMany(s => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .Select(s => s.Trim())
            .ToArray();

        return list.Length == 0 ? null : JsonSerializer.Serialize(list);
    }

    private async Task LoadRealmsAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            RealmOptions = new List<SelectListItem>();
            return;
        }

        var realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenantId.Value)
            .OrderBy(r => r.Name)
            .ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
    }

    private async Task LoadScopesAsync(Guid clientId)
    {
        // Get current tenant context for scope resolution
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;

        // Get available scopes for current tenant context using scope resolver
        var availableScopes = await scopeResolver.GetAvailableScopesAsync(currentTenantId);
        var availableScopeNames = availableScopes.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        // Get assigned scopes for this client
        AssignedScopes = await db.ClientScopes.AsNoTracking()
            .Where(cs => cs.ClientId == clientId)
            .Select(cs => cs.ScopeName)
            .OrderBy(n => n)
            .ToListAsync();

        // Filter available list to those not yet assigned
        var availableScopeObjects = availableScopes
            .Where(s => !AssignedScopes.Contains(s.Name, StringComparer.Ordinal))
            .OrderBy(s => s.IsGlobal ? 0 : 1) // Global scopes first
            .ThenBy(s => s.Name)
            .ToList();

        // Group available scopes
        AvailableScopes = availableScopeObjects;
        GlobalAvailableScopes = availableScopeObjects.Where(s => s.IsGlobal).ToList();
        TenantAvailableScopes = availableScopeObjects.Where(s => !s.IsGlobal).ToList();

        // Group assigned scopes by checking if they're standard scopes
        GlobalAssignedScopes = AssignedScopes.Where(s => scopeResolver.IsStandardScope(s)).OrderBy(s => s).ToList();
        TenantAssignedScopes = AssignedScopes.Where(s => !scopeResolver.IsStandardScope(s)).OrderBy(s => s).ToList();
    }

    private async Task LoadProviderMappingsAsync(Guid clientId)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            ProviderOptions = new List<SelectListItem>();
            ProviderMappings = new List<ProviderRow>();
            return;
        }

        ProviderOptions = await db.IdentityProviders.AsNoTracking()
            .Where(p => p.TenantId == currentTenantId.Value)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new SelectListItem(p.DisplayName ?? p.Name, p.Id.ToString()))
            .ToListAsync();

        ProviderMappings = await db.ClientIdentityProviders.AsNoTracking()
            .Where(m => m.ClientId == clientId)
            .Join(db.IdentityProviders.AsNoTracking().Where(p => p.TenantId == currentTenantId.Value), m => m.IdentityProviderId, p => p.Id, (m, p) => new ProviderRow
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

    private async Task LoadClientSecretsAsync(Guid clientId)
    {
        ClientSecrets = await db.ClientSecrets
            .AsNoTracking()
            .Where(s => s.ClientId == clientId)
            .OrderByDescending(s => s.CreatedAtUtc)
            .Select(s => new ClientSecretViewModel
            {
                Id = s.Id,
                Description = s.Description,
                CreatedAtUtc = s.CreatedAtUtc,
                ActivatedAtUtc = s.ActivatedAtUtc,
                ExpiresAtUtc = s.ExpiresAtUtc,
                RevokedAtUtc = s.RevokedAtUtc,
                IsPrimary = s.IsPrimary,
                CreatedBy = s.CreatedBy,
                ActivatedBy = s.ActivatedBy,
                RevokedBy = s.RevokedBy,
                LastUsedAtUtc = s.LastUsedAtUtc,
                UsageCount = s.UsageCount,
                Status = s.RevokedAtUtc != null ? "revoked" :
                         (s.ExpiresAtUtc != null && s.ExpiresAtUtc < DateTime.UtcNow) ? "expired" :
                         s.ActivatedAtUtc == null ? "inactive" :
                         s.IsPrimary ? "primary" : "active"
            })
            .ToListAsync();
    }

    private async Task LoadUserAssignmentsAsync(Guid clientId, Guid realmId, Guid tenantId)
    {
        // Get users already assigned to this client (filter by clientId only - realm is implicit from client)
        var assignedUserIds = await db.UserClientAssignments
            .AsNoTracking()
            .Where(a => a.ClientId == clientId)
            .Select(a => new { a.UserId, a.IsActive })
            .ToListAsync();

        var assignedUserIdSet = assignedUserIds.Select(a => a.UserId).ToHashSet();

        // Get assigned users with their details
        AssignedUsers = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && assignedUserIdSet.Contains(u.Id))
            .Select(u => new UserAssignmentViewModel
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Name = u.Name,
                IsActive = true // Will be updated below
            })
            .ToListAsync();

        // Update IsActive flag based on assignment status
        var assignmentLookup = assignedUserIds.ToDictionary(a => a.UserId, a => a.IsActive);
        foreach (var user in AssignedUsers)
        {
            if (assignmentLookup.TryGetValue(user.Id, out var isActive))
            {
                user.IsActive = isActive;
            }
        }

        // Get available users (not assigned to this client, same tenant)
        AvailableUsers = await db.Users
            .AsNoTracking()
            .Where(u => u.TenantId == tenantId && !assignedUserIdSet.Contains(u.Id))
            .Select(u => new UserAssignmentViewModel
            {
                Id = u.Id,
                Username = u.Username,
                Email = u.Email,
                Name = u.Name,
                IsActive = true
            })
            .ToListAsync();
    }

    private async Task<Client?> LoadClientEntityAsync(Guid clientId)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return null;
        }

        return await db.Clients.AsNoTracking()
            .Where(c => c.Id == clientId && c.TenantId == currentTenantId.Value)
            .FirstOrDefaultAsync();
    }

    private async Task ReloadPageDataAsync()
    {
        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        await LoadProviderMappingsAsync(Id);
        await LoadClientSecretsAsync(Id);

        var client = await LoadClientEntityAsync(Id);
        if (client is not null)
        {
            var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
            if (currentTenantId.HasValue)
            {
                await LoadUserAssignmentsAsync(client.Id, client.RealmId, currentTenantId.Value);
            }

            ClientDisplayName = client.ClientName ?? client.ClientId;
            ClientPublicId = client.ClientId;
#pragma warning disable CS0618
            HasLegacyClientSecretHash = !string.IsNullOrEmpty(client.ClientSecretHash);
#pragma warning restore CS0618
        }

        KeyPreviews = BuildPreviews(Input.PublicJwksJson);
        JwksStatus = ComputeJwksStatus(Input.PublicJwksJson);
    }

    public async Task<IActionResult> OnPostAddProviderAsync()
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

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
        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=providers");
    }

    public async Task<IActionResult> OnPostDeleteProviderAsync(Guid providerId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        await LoadRealmsAsync();
        await LoadScopesAsync(Id);
        await LoadProviderMappingsAsync(Id);

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            db.ClientIdentityProviders.Remove(entity);
            await db.SaveChangesAsync();
        }
        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=providers");
    }

    public async Task<IActionResult> OnPostToggleProviderEnabledAsync(Guid providerId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            entity.Enabled = !entity.Enabled;
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=providers");
    }

    public async Task<IActionResult> OnPostToggleProviderDefaultAsync(Guid providerId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            var newValue = !entity.IsDefaultForClient;
            if (newValue)
            {
                var mappings = await db.ClientIdentityProviders.Where(m => m.ClientId == Id).ToListAsync();
                foreach (var mapping in mappings)
                {
                    mapping.IsDefaultForClient = mapping.IdentityProviderId == providerId;
                }
            }
            else
            {
                entity.IsDefaultForClient = false;
            }

            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=providers");
    }

    public async Task<IActionResult> OnPostToggleProviderAutoAsync(Guid providerId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var entity = await db.ClientIdentityProviders.FirstOrDefaultAsync(m => m.ClientId == Id && m.IdentityProviderId == providerId);
        if (entity is not null)
        {
            entity.AutoRedirectIfSingle = !entity.AutoRedirectIfSingle;
            await db.SaveChangesAsync();
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=providers");
    }

    public async Task<IActionResult> OnPostCreateSecretAsync()
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var client = await LoadClientEntityAsync(Id);
        if (client is null)
        {
            return NotFound();
        }

        try
        {
            var activeCount = await db.ClientSecrets
                .Where(s => s.ClientId == Id && s.ActivatedAtUtc != null && s.RevokedAtUtc == null)
                .CountAsync();

            if (activeCount >= 3)
            {
                SecretErrorMessage = "Maximum active secrets reached (3). Revoke an existing secret before creating a new one.";
                await ReloadPageDataAsync();
                ActiveTab = "secrets";
                return Page();
            }

            if (SecretInput.ExpiresInDays.HasValue && (SecretInput.ExpiresInDays.Value < 1 || SecretInput.ExpiresInDays.Value > 730))
            {
                SecretErrorMessage = "Expiry must be between 1 and 730 days (2 years).";
                await ReloadPageDataAsync();
                ActiveTab = "secrets";
                return Page();
            }

            var secretValue = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            DateTime? expiresAtUtc = SecretInput.ExpiresInDays.HasValue
                ? DateTime.UtcNow.AddDays(SecretInput.ExpiresInDays.Value)
                : null;

            var username = User.Identity?.Name ?? "system";
            var secret = await clientStore.CreateSecretAsync(
                Id,
                secretValue,
                SecretInput.Description,
                username,
                expiresAtUtc);

            if (SecretInput.ActivateImmediately)
            {
                await clientStore.ActivateSecretAsync(secret.Id, username);
            }

            await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);

            SecretNewSecretValue = secretValue;
            TempData["SecretNewSecretIdentifier"] = secret.Id.ToString();
            SecretSuccessMessage = "Secret generated successfully. Save it now — you won't see it again!";
            SecretInput = new SecretInputModel();

            return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=secrets");
        }
        catch (Exception ex)
        {
            SecretErrorMessage = $"Failed to create secret: {ex.Message}";
            await ReloadPageDataAsync();
            ActiveTab = "secrets";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostActivateSecretAsync(Guid secretId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var client = await LoadClientEntityAsync(Id);
        if (client is null)
        {
            return NotFound();
        }

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await clientStore.ActivateSecretAsync(secretId, username);

            if (!success)
            {
                SecretErrorMessage = "Failed to activate secret. Secret not found.";
            }
            else
            {
                await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SecretSuccessMessage = "Secret activated successfully.";
            }
        }
        catch (Exception ex)
        {
            SecretErrorMessage = $"Failed to activate secret: {ex.Message}";
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=secrets");
    }

    public async Task<IActionResult> OnPostSetPrimarySecretAsync(Guid secretId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var client = await LoadClientEntityAsync(Id);
        if (client is null)
        {
            return NotFound();
        }

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await clientStore.SetPrimarySecretAsync(secretId, username);

            if (!success)
            {
                SecretErrorMessage = "Failed to set primary secret. Secret not found or not active.";
            }
            else
            {
                await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SecretSuccessMessage = "Primary secret updated successfully.";
            }
        }
        catch (Exception ex)
        {
            SecretErrorMessage = $"Failed to set primary secret: {ex.Message}";
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=secrets");
    }

    public async Task<IActionResult> OnPostRevokeSecretAsync(Guid secretId)
    {
        if (!await ValidateTenantAccessAsync())
        {
            return NotFound();
        }

        var client = await LoadClientEntityAsync(Id);
        if (client is null)
        {
            return NotFound();
        }

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await clientStore.RevokeSecretAsync(secretId, username);

            if (!success)
            {
                SecretErrorMessage = "Failed to revoke secret. Cannot revoke the last active secret (would lock out client).";
            }
            else
            {
                await clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SecretSuccessMessage = "Secret revoked successfully.";
            }
        }
        catch (Exception ex)
        {
            SecretErrorMessage = $"Failed to revoke secret: {ex.Message}";
        }

        return TenantAwareRedirect($"/Admin/Clients/Edit/{Id}?tab=secrets");
    }

    public class SecretInputModel
    {
        public string? Description { get; set; }
        public int? ExpiresInDays { get; set; }
        public bool ActivateImmediately { get; set; } = true;
    }

    public class ClientSecretViewModel
    {
        public Guid Id { get; set; }
        public string? Description { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ActivatedAtUtc { get; set; }
        public DateTime? ExpiresAtUtc { get; set; }
        public DateTime? RevokedAtUtc { get; set; }
        public bool IsPrimary { get; set; }
        public string? CreatedBy { get; set; }
        public string? ActivatedBy { get; set; }
        public string? RevokedBy { get; set; }
        public DateTime? LastUsedAtUtc { get; set; }
        public long UsageCount { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    /// <summary>
    /// Validates that the current user has access to the client based on tenant filtering.
    /// Returns true if access is allowed (platform admin or client belongs to user's tenant).
    /// </summary>
    private async Task<bool> ValidateTenantAccessAsync()
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return false; // No tenant context
        }

        // Check if client belongs to the current tenant
        return await db.Clients.AnyAsync(c => c.Id == Id && c.TenantId == currentTenantId.Value);
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

        [Display(Name = "Subject type")]
        [Required]
        [StringLength(20)]
        public string SubjectType { get; set; } = OidcConstants.SubjectTypes.Public;

        [Display(Name = "Sector identifier URI")]
        [StringLength(2000)]
        [Url]
        public string? SectorIdentifierUri { get; set; }

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

        [Display(Name = "ID token signed response alg")]
        [StringLength(50)]
        public string? IdTokenSignedResponseAlg { get; set; }

        [Display(Name = "ID token encrypted response alg")]
        [StringLength(50)]
        public string? IdTokenEncryptedResponseAlg { get; set; }

        [Display(Name = "ID token encrypted response enc")]
        [StringLength(50)]
        public string? IdTokenEncryptedResponseEnc { get; set; }

        [Display(Name = "UserInfo signed response alg")]
        [StringLength(50)]
        public string? UserInfoSignedResponseAlg { get; set; }

        [Display(Name = "UserInfo encrypted response alg")]
        [StringLength(50)]
        public string? UserInfoEncryptedResponseAlg { get; set; }

        [Display(Name = "UserInfo encrypted response enc")]
        [StringLength(50)]
        public string? UserInfoEncryptedResponseEnc { get; set; }

        [Display(Name = "Authorization signed response alg")]
        [StringLength(50)]
        public string? AuthorizationSignedResponseAlg { get; set; }

        [Display(Name = "Authorization encrypted response alg")]
        [StringLength(50)]
        public string? AuthorizationEncryptedResponseAlg { get; set; }

        [Display(Name = "Authorization encrypted response enc")]
        [StringLength(50)]
        public string? AuthorizationEncryptedResponseEnc { get; set; }
        [Display(Name = "Test signed JWT")]
        public string? TestJwt { get; set; }
        [Display(Name = "Private JWK or JWKS (one-time)")]
        public string? PrivateJwk { get; set; }

        [Display(Name = "Allowed login redirect URIs (comma-separated)")]
        public string? AllowedLoginRedirectUris { get; set; }
        [Display(Name = "Allowed logout redirect URIs (comma-separated)")]
        public string? AllowedLogoutRedirectUris { get; set; }

        [Display(Name = "Back-channel logout URI")]
        public string? BackChannelLogoutUri { get; set; }
        [Display(Name = "Back-channel logout session required")]
        public bool BackChannelLogoutSessionRequired { get; set; } = true;

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

        // New: Auto-approval for new registrations
        [Display(Name = "Auto-approve new registrations")]
        public AutoApprovalMode AutoApprovalMode { get; set; } = AutoApprovalMode.No;

        [Display(Name = "Auto-assign new users to this client")]
        public bool AutoAssignNewUsersToClient { get; set; } = false;

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

        // OBO policy editing
        [Display(Name = "Enable Token Exchange / OBO")]
        public bool OboEnabled { get; set; } = true;
        [Display(Name = "OBO allowed callers (client_ids, comma-separated)")]
        public string? OboAllowedCallers { get; set; }
        [Display(Name = "OBO allowed source audiences (comma-separated)")]
        public string? OboAllowedSourceAudiences { get; set; }
        [Display(Name = "OBO allowed target audiences (comma-separated)")]
        public string? OboAllowedTargetAudiences { get; set; }
        [Display(Name = "OBO allowed scopes (comma-separated)")]
        public string? OboAllowedScopes { get; set; }
        [Display(Name = "OBO max delegation depth (0 or empty = default 1)")]
        [Range(0, 10)]
        public int? OboMaxDelegationDepth { get; set; }
        [Display(Name = "OBO max lifetime (minutes, 0 or empty = default 15)")]
        [Range(0, 1440)]
        public int? OboMaxLifetimeMinutes { get; set; }
        [Display(Name = "DPoP bridging mode")]
        public OboDpopMode OboDpopMode { get; set; } = OboDpopMode.Deny;
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

    public sealed class UserAssignmentViewModel
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Name { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
