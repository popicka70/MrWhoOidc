using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

[Authorize(Policy = "tenant-admin")]
public class SecretsModel : PageModel
{
    private readonly AuthDbContext _db;
    private readonly IClientStore _clientStore;
    private readonly ITenantAccessor _tenantAccessor;
    private readonly IAuthorizationService _authorizationService;

    public SecretsModel(
        AuthDbContext db,
        IClientStore clientStore,
        ITenantAccessor tenantAccessor,
        IAuthorizationService authorizationService)
    {
        _db = db;
        _clientStore = clientStore;
        _tenantAccessor = tenantAccessor;
        _authorizationService = authorizationService;
    }

    [BindProperty(SupportsGet = true)]
    public Guid Id { get; set; }

    public Guid ClientId => Id;
    public string ClientName { get; set; } = string.Empty;
    public string ClientClientId { get; set; } = string.Empty;

    public List<ClientSecretViewModel> Secrets { get; set; } = new();

    [TempData]
    public string? ErrorMessage { get; set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? NewSecretValue { get; set; }

    [TempData]
    public string? NewSecretId { get; set; }

    [BindProperty]
    public CreateSecretInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var client = await LoadClientAsync();
        if (client == null) return NotFound();

        await LoadSecretsAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync()
    {
        var client = await LoadClientAsync();
        if (client == null) return NotFound();

        try
        {
            // Check max active secrets limit (3)
            var activeCount = await _db.ClientSecrets
                .Where(s => s.ClientId == Id && s.ActivatedAtUtc != null && s.RevokedAtUtc == null)
                .CountAsync();

            if (activeCount >= 3)
            {
                ErrorMessage = "Maximum active secrets reached (3). Revoke an existing secret before creating a new one.";
                await LoadSecretsAsync();
                return Page();
            }

            // Validate expiry
            if (Input.ExpiresInDays.HasValue && (Input.ExpiresInDays.Value < 1 || Input.ExpiresInDays.Value > 730))
            {
                ErrorMessage = "Expiry must be between 1 and 730 days (2 years).";
                await LoadSecretsAsync();
                return Page();
            }

            // Generate secure random secret (32 bytes = 256 bits)
            var secretValue = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

            // Calculate expiry
            DateTime? expiresAtUtc = null;
            if (Input.ExpiresInDays.HasValue)
            {
                expiresAtUtc = DateTime.UtcNow.AddDays(Input.ExpiresInDays.Value);
            }

            // Get current user
            var username = User.Identity?.Name ?? "system";

            // Create secret
            var secret = await _clientStore.CreateSecretAsync(
                Id,
                secretValue,
                Input.Description,
                username,
                expiresAtUtc);

            // Activate immediately if requested
            if (Input.ActivateImmediately)
            {
                await _clientStore.ActivateSecretAsync(secret.Id, username);
            }

            // Invalidate cache
            await _clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);

            // Store secret value in TempData to display once
            NewSecretValue = secretValue;
            NewSecretId = secret.Id.ToString();
            SuccessMessage = "Secret generated successfully. Save it now — you won't see it again!";

            return RedirectToPage(new { id = Id });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to create secret: {ex.Message}";
            await LoadSecretsAsync();
            return Page();
        }
    }

    public async Task<IActionResult> OnPostActivateAsync(Guid secretId)
    {
        var client = await LoadClientAsync();
        if (client == null) return NotFound();

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await _clientStore.ActivateSecretAsync(secretId, username);

            if (!success)
            {
                ErrorMessage = "Failed to activate secret. Secret not found.";
            }
            else
            {
                await _clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SuccessMessage = "Secret activated successfully.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to activate secret: {ex.Message}";
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostSetPrimaryAsync(Guid secretId)
    {
        var client = await LoadClientAsync();
        if (client == null) return NotFound();

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await _clientStore.SetPrimarySecretAsync(secretId, username);

            if (!success)
            {
                ErrorMessage = "Failed to set primary secret. Secret not found or not active.";
            }
            else
            {
                await _clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SuccessMessage = "Primary secret updated successfully.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to set primary secret: {ex.Message}";
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid secretId)
    {
        var client = await LoadClientAsync();
        if (client == null) return NotFound();

        try
        {
            var username = User.Identity?.Name ?? "system";
            var success = await _clientStore.RevokeSecretAsync(secretId, username);

            if (!success)
            {
                ErrorMessage = "Failed to revoke secret. Cannot revoke the last active secret (would lock out client).";
            }
            else
            {
                await _clientStore.InvalidateClientCacheAsync(client.ClientId, client.TenantId);
                SuccessMessage = "Secret revoked successfully.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to revoke secret: {ex.Message}";
        }

        return RedirectToPage(new { id = Id });
    }

    private async Task<Client?> LoadClientAsync()
    {
        var platformAdminResult = await _authorizationService.AuthorizeAsync(User, "platform-admin");
        var isPlatformAdmin = platformAdminResult.Succeeded;

        var query = _db.Clients.AsNoTracking().Where(c => c.Id == Id);

        if (!isPlatformAdmin)
        {
            var currentTenantId = _tenantAccessor.CurrentTenant?.TenantId;
            if (!currentTenantId.HasValue) return null;
            query = query.Where(c => c.TenantId == currentTenantId.Value);
        }

        var client = await query.FirstOrDefaultAsync();
        if (client != null)
        {
            ClientName = client.ClientName ?? client.ClientId;
            ClientClientId = client.ClientId;
        }

        return client;
    }

    private async Task LoadSecretsAsync()
    {
        Secrets = await _db.ClientSecrets
            .AsNoTracking()
            .Where(s => s.ClientId == Id)
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
                         s.ExpiresAtUtc != null && s.ExpiresAtUtc < DateTime.UtcNow ? "expired" :
                         s.ActivatedAtUtc == null ? "inactive" :
                         s.IsPrimary ? "primary" : "active"
            })
            .ToListAsync();
    }

    public class CreateSecretInput
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
}
