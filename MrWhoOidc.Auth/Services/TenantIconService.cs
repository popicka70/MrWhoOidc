using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

public class TenantIconService : ITenantIconService
{
    private readonly AuthDbContext _db;
    private readonly ILogger<TenantIconService> _logger;

    public TenantIconService(AuthDbContext db, ILogger<TenantIconService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<Guid> UploadIconAsync(Guid tenantId, string fileName, string contentType, byte[] fileData, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting icon upload for tenant {TenantId}", tenantId);

        try
        {
            // Simple validation
            if (fileData == null || fileData.Length == 0)
                throw new ArgumentException("File data cannot be empty");
            
            if (fileData.Length > 2 * 1024 * 1024) // 2MB limit
                throw new ArgumentException("File too large");

            // Use the execution strategy to handle the entire operation as a transaction
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                // Verify tenant exists
                var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
                if (tenant == null)
                    throw new InvalidOperationException($"Tenant {tenantId} not found");

                // Delete existing icon if any
                if (tenant.TenantIconId.HasValue)
                {
                    var existingIcon = await _db.TenantIcons.FirstOrDefaultAsync(i => i.Id == tenant.TenantIconId.Value, cancellationToken);
                    if (existingIcon != null)
                    {
                        _db.TenantIcons.Remove(existingIcon);
                    }
                }

                // Create new icon
                var newIcon = new TenantIcon
                {
                    TenantId = tenantId,
                    FileName = fileName,
                    ContentType = contentType,
                    FileData = fileData,
                    FileSize = fileData.Length,
                    Width = 100,
                    Height = 100,
                    UploadedAt = DateTimeOffset.UtcNow
                };

                _db.TenantIcons.Add(newIcon);
                tenant.TenantIconId = newIcon.Id;

                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation("Successfully uploaded icon {IconId} for tenant {TenantId}", newIcon.Id, tenantId);
                return newIcon.Id;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload icon for tenant {TenantId}", tenantId);
            throw;
        }
    }

    public async Task<TenantIconData?> GetIconAsync(Guid iconId, CancellationToken cancellationToken = default)
    {
        var icon = await _db.TenantIcons
            .Where(i => i.Id == iconId)
            .Select(i => new TenantIconData
            {
                Id = i.Id,
                FileName = i.FileName,
                ContentType = i.ContentType,
                FileData = i.FileData,
                FileSize = i.FileSize,
                Width = i.Width,
                Height = i.Height,
                UploadedAt = i.UploadedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        return icon;
    }

    public async Task<TenantIconData?> GetTenantIconAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var icon = await _db.TenantIcons
            .Where(i => i.TenantId == tenantId)
            .Select(i => new TenantIconData
            {
                Id = i.Id,
                FileName = i.FileName,
                ContentType = i.ContentType,
                FileData = i.FileData,
                FileSize = i.FileSize,
                Width = i.Width,
                Height = i.Height,
                UploadedAt = i.UploadedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        return icon;
    }

    public async Task<bool> DeleteTenantIconAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        try
        {
            var strategy = _db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);
                if (tenant?.TenantIconId == null)
                    return false;

                var icon = await _db.TenantIcons.FirstOrDefaultAsync(i => i.Id == tenant.TenantIconId.Value, cancellationToken);
                if (icon == null)
                    return false;

                _db.TenantIcons.Remove(icon);
                tenant.TenantIconId = null;
                
                await _db.SaveChangesAsync(cancellationToken);
                return true;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete icon for tenant {TenantId}", tenantId);
            throw;
        }
    }
}
