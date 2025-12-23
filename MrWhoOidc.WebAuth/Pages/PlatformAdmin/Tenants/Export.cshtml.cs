using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Security.Admin;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.PlatformAdmin.Tenants;

[Authorize(Policy = "platform-admin")]
[RequireDefaultTenantContext]
public class ExportModel(
    AuthDbContext db,
    IConfigurationExportService exportService) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public TenantInfo Tenant { get; private set; } = default!;

    public ExportPreviewInfo Preview { get; private set; } = default!;

    [BindProperty]
    public string ExportMode { get; set; } = "obfuscated";

    public string? ErrorMessage { get; private set; }

    public class TenantInfo
    {
        public Guid Id { get; set; }
        public string Slug { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
    }

    public class ExportPreviewInfo
    {
        public int RealmCount { get; set; }
        public int ClientCount { get; set; }
        public int ProviderCount { get; set; }
        public int ScopeCount { get; set; }
        public int RoleCount { get; set; }
        public int SecretCount { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

        Tenant = new TenantInfo
        {
            Id = tenant.Id,
            Slug = tenant.Slug,
            Name = tenant.Name,
            Description = tenant.Description
        };

        // Load preview counts
        Preview = await LoadPreviewAsync(tenant.Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id);

        if (tenant == null)
        {
            return NotFound();
        }

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
            var manifest = await exportService.ExportTenantAsync(tenant.Id, options);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant.Slug}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            Tenant = new TenantInfo
            {
                Id = tenant.Id,
                Slug = tenant.Slug,
                Name = tenant.Name,
                Description = tenant.Description
            };
            Preview = await LoadPreviewAsync(tenant.Id);
            ErrorMessage = $"Export failed: {ex.Message}";
            return Page();
        }
    }

    private async Task<ExportPreviewInfo> LoadPreviewAsync(Guid tenantId)
    {
        var realmCount = await db.Realms
            .CountAsync(r => r.TenantId == tenantId);

        var clientCount = await db.Clients
            .CountAsync(c => c.TenantId == tenantId);

        var providerCount = await db.IdentityProviders
            .CountAsync(p => p.TenantId == tenantId);

        var scopeCount = await db.Scopes
            .CountAsync(s => s.TenantId == tenantId);

        var realmIds = await db.Realms
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.Id)
            .ToListAsync();

        var roleCount = await db.Roles
            .CountAsync(r => realmIds.Contains(r.RealmId));

        var clientIds = await db.Clients
            .Where(c => c.TenantId == tenantId)
            .Select(c => c.Id)
            .ToListAsync();

        var secretCount = await db.ClientSecrets
            .CountAsync(s => clientIds.Contains(s.ClientId));

        return new ExportPreviewInfo
        {
            RealmCount = realmCount,
            ClientCount = clientCount,
            ProviderCount = providerCount,
            ScopeCount = scopeCount,
            RoleCount = roleCount,
            SecretCount = secretCount
        };
    }
}
