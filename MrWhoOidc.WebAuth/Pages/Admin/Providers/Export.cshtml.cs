using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class ExportModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions,
    IConfigurationExportService exportService) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ProviderInfo Provider { get; private set; } = default!;

    public ExportPreviewInfo Preview { get; private set; } = default!;

    [BindProperty]
    public string ExportMode { get; set; } = "obfuscated";

    public string? ErrorMessage { get; private set; }

    public class ProviderInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string ProviderType { get; set; } = string.Empty;
        public string TenantSlug { get; set; } = string.Empty;
    }

    public class ExportPreviewInfo
    {
        public int ClaimMappingCount { get; set; }
        public int KeyCount { get; set; }
        public bool HasClientSecret { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var provider = await db.IdentityProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == currentTenantId.Value);

        if (provider == null)
        {
            return NotFound();
        }

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == currentTenantId.Value);

        Provider = new ProviderInfo
        {
            Id = provider.Id,
            Name = provider.Name,
            DisplayName = provider.DisplayName,
            ProviderType = provider.Type.ToString(),
            TenantSlug = tenant?.Slug ?? "unknown"
        };

        Preview = await LoadPreviewAsync(provider.Id, provider.ConfigJson);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var provider = await db.IdentityProviders
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.TenantId == currentTenantId.Value);

        if (provider == null)
        {
            return NotFound();
        }

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == currentTenantId.Value);

        var mode = ExportMode?.ToLowerInvariant() switch
        {
            "full" => MrWhoOidc.Auth.Seeding.ExportMode.Full,
            _ => MrWhoOidc.Auth.Seeding.ExportMode.Obfuscated
        };

        var options = new ExportOptions
        {
            Mode = mode,
            IncludeMetadata = true,
            IncludeChecksum = true,
            PrettyPrint = true,
            ExportedBy = User.Identity?.Name ?? "anonymous",
            SourceSystem = Environment.MachineName
        };

        try
        {
            var manifest = await exportService.ExportIdentityProviderAsync(provider.Id, options);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{provider.Name}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            Provider = new ProviderInfo
            {
                Id = provider.Id,
                Name = provider.Name,
                DisplayName = provider.DisplayName,
                ProviderType = provider.Type.ToString(),
                TenantSlug = tenant?.Slug ?? "unknown"
            };
            Preview = await LoadPreviewAsync(provider.Id, provider.ConfigJson);
            ErrorMessage = $"Export failed: {ex.Message}";
            return Page();
        }
    }

    private async Task<ExportPreviewInfo> LoadPreviewAsync(Guid providerId, string? configJson)
    {
        var claimMappingCount = await db.IdentityProviderClaimMappings
            .CountAsync(m => m.IdentityProviderId == providerId);

        var keyCount = await db.IdentityProviderKeys
            .CountAsync(k => k.IdentityProviderId == providerId);

        // Check if config contains client_secret by parsing JSON (simple check)
        var hasClientSecret = !string.IsNullOrEmpty(configJson) && configJson.Contains("client_secret", StringComparison.OrdinalIgnoreCase);

        return new ExportPreviewInfo
        {
            ClaimMappingCount = claimMappingCount,
            KeyCount = keyCount,
            HasClientSecret = hasClientSecret
        };
    }
}
