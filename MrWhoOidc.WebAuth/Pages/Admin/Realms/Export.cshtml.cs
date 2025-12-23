using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.Admin.Realms;

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

    public RealmInfo Realm { get; private set; } = default!;

    public ExportPreviewInfo Preview { get; private set; } = default!;

    [BindProperty]
    public string ExportMode { get; set; } = "obfuscated";

    public string? ErrorMessage { get; private set; }

    public class RealmInfo
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? DisplayName { get; set; }
        public string TenantSlug { get; set; } = string.Empty;
    }

    public class ExportPreviewInfo
    {
        public int ClientCount { get; set; }
        public int RoleCount { get; set; }
        public int SecretCount { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var realm = await db.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);

        if (realm == null)
        {
            return NotFound();
        }

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == currentTenantId.Value);

        Realm = new RealmInfo
        {
            Id = realm.Id,
            Name = realm.Name,
            DisplayName = realm.DisplayName,
            TenantSlug = tenant?.Slug ?? "unknown"
        };

        Preview = await LoadPreviewAsync(realm.Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var realm = await db.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.TenantId == currentTenantId.Value);

        if (realm == null)
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
            var manifest = await exportService.ExportRealmAsync(realm.Id, options);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{realm.Name}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            Realm = new RealmInfo
            {
                Id = realm.Id,
                Name = realm.Name,
                DisplayName = realm.DisplayName,
                TenantSlug = tenant?.Slug ?? "unknown"
            };
            Preview = await LoadPreviewAsync(realm.Id);
            ErrorMessage = $"Export failed: {ex.Message}";
            return Page();
        }
    }

    private async Task<ExportPreviewInfo> LoadPreviewAsync(Guid realmId)
    {
        var clientCount = await db.Clients
            .CountAsync(c => c.RealmId == realmId);

        var roleCount = await db.Roles
            .CountAsync(r => r.RealmId == realmId);

        var clientIds = await db.Clients
            .Where(c => c.RealmId == realmId)
            .Select(c => c.Id)
            .ToListAsync();

        var secretCount = await db.ClientSecrets
            .CountAsync(s => clientIds.Contains(s.ClientId));

        return new ExportPreviewInfo
        {
            ClientCount = clientCount,
            RoleCount = roleCount,
            SecretCount = secretCount
        };
    }
}
