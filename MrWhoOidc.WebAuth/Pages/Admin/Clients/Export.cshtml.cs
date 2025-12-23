using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.Auth.Services;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Pages.Admin.Clients;

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

    public ClientInfo Client { get; private set; } = default!;

    public ExportPreviewInfo Preview { get; private set; } = default!;

    [BindProperty]
    public string ExportMode { get; set; } = "obfuscated";

    public string? ErrorMessage { get; private set; }

    public class ClientInfo
    {
        public Guid Id { get; set; }
        public string ClientId { get; set; } = string.Empty;
        public string? ClientName { get; set; }
        public string RealmName { get; set; } = string.Empty;
        public string TenantSlug { get; set; } = string.Empty;
    }

    public class ExportPreviewInfo
    {
        public int ScopeCount { get; set; }
        public int SecretCount { get; set; }
        public int IdpAssignmentCount { get; set; }
    }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var client = await db.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == currentTenantId.Value);

        if (client == null)
        {
            return NotFound();
        }

        var realm = await db.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == client.RealmId);

        var tenant = await db.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == currentTenantId.Value);

        Client = new ClientInfo
        {
            Id = client.Id,
            ClientId = client.ClientId,
            ClientName = client.ClientName,
            RealmName = realm?.Name ?? "unknown",
            TenantSlug = tenant?.Slug ?? "unknown"
        };

        Preview = await LoadPreviewAsync(client.Id);

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id)
    {
        var currentTenantId = TenantAccessor.CurrentTenant?.TenantId;
        if (!currentTenantId.HasValue)
        {
            return NotFound();
        }

        var client = await db.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id && c.TenantId == currentTenantId.Value);

        if (client == null)
        {
            return NotFound();
        }

        var realm = await db.Realms
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == client.RealmId);

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
            var manifest = await exportService.ExportClientAsync(client.Id, options);

            var json = JsonSerializer.Serialize(manifest, JsonOptions);
            var fileName = $"{tenant?.Slug ?? "tenant"}-{realm?.Name ?? "realm"}-{client.ClientId}-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

            return File(
                System.Text.Encoding.UTF8.GetBytes(json),
                "application/json",
                fileName);
        }
        catch (Exception ex)
        {
            Client = new ClientInfo
            {
                Id = client.Id,
                ClientId = client.ClientId,
                ClientName = client.ClientName,
                RealmName = realm?.Name ?? "unknown",
                TenantSlug = tenant?.Slug ?? "unknown"
            };
            Preview = await LoadPreviewAsync(client.Id);
            ErrorMessage = $"Export failed: {ex.Message}";
            return Page();
        }
    }

    private async Task<ExportPreviewInfo> LoadPreviewAsync(Guid clientId)
    {
        var scopeCount = await db.ClientScopes
            .CountAsync(cs => cs.ClientId == clientId);

        var secretCount = await db.ClientSecrets
            .CountAsync(s => s.ClientId == clientId);

        var idpAssignmentCount = await db.ClientIdentityProviders
            .CountAsync(cip => cip.ClientId == clientId);

        return new ExportPreviewInfo
        {
            ScopeCount = scopeCount,
            SecretCount = secretCount,
            IdpAssignmentCount = idpAssignmentCount
        };
    }
}
