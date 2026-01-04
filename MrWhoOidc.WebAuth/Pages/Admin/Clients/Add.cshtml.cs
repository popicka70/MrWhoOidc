using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db, 
    IPasswordHasher hasher, 
    IClientIdGenerator idGen, 
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public List<SelectListItem> RealmOptions { get; private set; } = new();

    public string ActiveSigningAlg { get; private set; } = SecurityConstants.JwtAlgorithms.RS256;

    [BindProperty]
    public ClientInput Input { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadRealmsAsync();
        await LoadActiveSigningAlgAsync();
    }

    // Explicit create handler to avoid any ambiguity with other submit buttons
    public async Task<IActionResult> OnPostCreateAsync()
    {
        await LoadRealmsAsync();
        await LoadActiveSigningAlgAsync();

        ValidateJwtResponseCrypto();
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Unique client id
        if (await db.Clients.AnyAsync(c => c.ClientId == Input.ClientId))
        {
            ModelState.AddModelError("Input.ClientId", "Client ID already exists");
            return Page();
        }

        // Get current tenant ID from context
        var currentTenant = TenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            ModelState.AddModelError(string.Empty, "Unable to determine current tenant context");
            return Page();
        }

        var realmAllowed = await db.Realms.AnyAsync(r => r.Id == Input.RealmId && r.TenantId == currentTenant.TenantId);
        if (!realmAllowed)
        {
            ModelState.AddModelError("Input.RealmId", "Selected realm does not belong to the current tenant.");
            return Page();
        }

        var entity = new Client
        {
            ClientId = Input.ClientId,
            ClientName = string.IsNullOrWhiteSpace(Input.ClientName) ? null : Input.ClientName,
            TenantId = currentTenant.TenantId,
            RealmId = Input.RealmId,
            RequirePkce = Input.RequirePkce,
            RequireConsent = Input.RequireConsent,
            AutoAssignNewUsersToClient = Input.AutoAssignNewUsersToClient,
            PublicJwksUri = string.IsNullOrWhiteSpace(Input.PublicJwksUri) ? null : Input.PublicJwksUri,
            PublicJwksJson = string.IsNullOrWhiteSpace(Input.PublicJwksJson) ? null : Input.PublicJwksJson,
            IdTokenEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseAlg) ? null : Input.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseEnc) ? null : Input.IdTokenEncryptedResponseEnc,
            UserInfoSignedResponseAlg = string.IsNullOrWhiteSpace(Input.UserInfoSignedResponseAlg) ? null : Input.UserInfoSignedResponseAlg,
            UserInfoEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseAlg) ? null : Input.UserInfoEncryptedResponseAlg,
            UserInfoEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseEnc) ? null : Input.UserInfoEncryptedResponseEnc,
                AuthorizationSignedResponseAlg = string.IsNullOrWhiteSpace(Input.AuthorizationSignedResponseAlg) ? null : Input.AuthorizationSignedResponseAlg,
                AuthorizationEncryptedResponseAlg = string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseAlg) ? null : Input.AuthorizationEncryptedResponseAlg,
                AuthorizationEncryptedResponseEnc = string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseEnc) ? null : Input.AuthorizationEncryptedResponseEnc,
#pragma warning disable CS0618 // Type or member is obsolete - backward compatibility during migration
            ClientSecretHash = string.IsNullOrEmpty(Input.ClientSecret) ? null : hasher.Hash(Input.ClientSecret)
#pragma warning restore CS0618
        };
        db.Clients.Add(entity);
        await db.SaveChangesAsync();
        return TenantAwareRedirect($"/Admin/Clients/Edit/{entity.Id}");
    }

    public async Task<IActionResult> OnPostGenerateAsync()
    {
        await LoadRealmsAsync();
        await LoadActiveSigningAlgAsync();
        if (Input is null)
        {
            Input = new ClientInput();
        }
        Input.ClientId = idGen.Generate(24);
        ModelState.Remove("Input.ClientId");
        return Page();
    }

    private async Task LoadActiveSigningAlgAsync()
    {
        var tenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!tenantId.HasValue)
        {
            ActiveSigningAlg = SecurityConstants.JwtAlgorithms.RS256;
            return;
        }

        var alg = await db.SigningKeys
            .AsNoTracking()
            .Where(k => k.TenantId == tenantId.Value)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Alg)
            .FirstOrDefaultAsync();

        ActiveSigningAlg = string.IsNullOrWhiteSpace(alg) ? SecurityConstants.JwtAlgorithms.RS256 : alg;
    }

    private void ValidateJwtResponseCrypto()
    {
        ValidateUserInfoSignedResponseAlg();
        ValidateAuthorizationSignedResponseAlg();

        ValidateEncryptionPair(
            algKey: "Input.IdTokenEncryptedResponseAlg",
            encKey: "Input.IdTokenEncryptedResponseEnc",
            alg: Input.IdTokenEncryptedResponseAlg,
            enc: Input.IdTokenEncryptedResponseEnc);

        ValidateEncryptionPair(
            algKey: "Input.UserInfoEncryptedResponseAlg",
            encKey: "Input.UserInfoEncryptedResponseEnc",
            alg: Input.UserInfoEncryptedResponseAlg,
            enc: Input.UserInfoEncryptedResponseEnc);

        ValidateEncryptionPair(
            algKey: "Input.AuthorizationEncryptedResponseAlg",
            encKey: "Input.AuthorizationEncryptedResponseEnc",
            alg: Input.AuthorizationEncryptedResponseAlg,
            enc: Input.AuthorizationEncryptedResponseEnc);

        var encryptionEnabled = !string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseAlg)
            || !string.IsNullOrWhiteSpace(Input.IdTokenEncryptedResponseEnc)
            || !string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseAlg)
            || !string.IsNullOrWhiteSpace(Input.UserInfoEncryptedResponseEnc)
            || !string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseAlg)
            || !string.IsNullOrWhiteSpace(Input.AuthorizationEncryptedResponseEnc);

        if (encryptionEnabled && string.IsNullOrWhiteSpace(Input.PublicJwksJson) && string.IsNullOrWhiteSpace(Input.PublicJwksUri))
        {
            ModelState.AddModelError("Input.PublicJwksJson", "Client public JWKS is required for encrypted responses.");
            ModelState.AddModelError("Input.PublicJwksUri", "Client public JWKS is required for encrypted responses.");
        }
    }

    private void ValidateUserInfoSignedResponseAlg()
    {
        if (string.IsNullOrWhiteSpace(Input.UserInfoSignedResponseAlg))
        {
            return;
        }

        if (string.Equals(Input.UserInfoSignedResponseAlg, SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.UserInfoSignedResponseAlg", "'none' is not supported.");
            return;
        }

        if (!string.Equals(Input.UserInfoSignedResponseAlg, ActiveSigningAlg, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.UserInfoSignedResponseAlg", $"Must match tenant active signing alg: '{ActiveSigningAlg}'.");
        }
    }

    private void ValidateAuthorizationSignedResponseAlg()
    {
        if (string.IsNullOrWhiteSpace(Input.AuthorizationSignedResponseAlg))
        {
            return;
        }

        if (string.Equals(Input.AuthorizationSignedResponseAlg, SecurityAlgorithms.None, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError("Input.AuthorizationSignedResponseAlg", "'none' is not supported.");
            return;
        }

        if (!string.Equals(Input.AuthorizationSignedResponseAlg, ActiveSigningAlg, StringComparison.Ordinal))
        {
            ModelState.AddModelError("Input.AuthorizationSignedResponseAlg", $"Must match tenant active signing alg: '{ActiveSigningAlg}'.");
        }
    }

    private void ValidateEncryptionPair(string algKey, string encKey, string? alg, string? enc)
    {
        var algSet = !string.IsNullOrWhiteSpace(alg);
        var encSet = !string.IsNullOrWhiteSpace(enc);

        if (algSet && !encSet)
        {
            ModelState.AddModelError(encKey, "Select an encryption enc or clear the alg value.");
        }
        if (!algSet && encSet)
        {
            ModelState.AddModelError(algKey, "Select an encryption alg or clear the enc value.");
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

    private async Task LoadRealmsAsync()
    {
        // Get current tenant ID to filter realms
        var currentTenant = TenantAccessor.CurrentTenant;
        if (currentTenant == null)
        {
            RealmOptions = new List<SelectListItem>();
            return;
        }

        var realms = await db.Realms.AsNoTracking()
            .Where(r => r.TenantId == currentTenant.TenantId)
            .OrderBy(r => r.Name)
            .ToListAsync();
        RealmOptions = realms.Select(r => new SelectListItem(r.Name, r.Id.ToString())).ToList();
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

        [Display(Name = "Auto-assign new users to this client")]
        public bool AutoAssignNewUsersToClient { get; set; } = false;

        [DataType(DataType.Password)]
        public string? ClientSecret { get; set; }

        [Display(Name = "Public JWKS URI")]
        [Url]
        public string? PublicJwksUri { get; set; }

        [Display(Name = "Public JWKS JSON")]
        public string? PublicJwksJson { get; set; }

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
    }
}
