using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Finds the device authorization grant's entries by their codes. M-Plain: the codes are not stored in plaintext,
/// so the lookup uses the stored lookup value instead of the code itself.
/// </summary>
public interface IDeviceCodeStore
{
    /// <summary>Finds the entry of a device_code (the value the client polls with).</summary>
    Task<DeviceCodeEntry?> FindByDeviceCodeAsync(Guid tenantId, string deviceCode, CancellationToken ct = default);

    /// <summary>Finds the entry of a user_code (the value the user types on the verification page).</summary>
    Task<DeviceCodeEntry?> FindByUserCodeAsync(Guid tenantId, string userCode, CancellationToken ct = default);
}

public sealed class DeviceCodeStore(
    AuthDbContext db,
    IDeviceCodeProtector protector,
    ILogger<DeviceCodeStore> logger) : IDeviceCodeStore
{
    /// <summary>
    /// How many live device codes are compared when the user code cannot be looked up by its stored value. The scan
    /// is bounded, so a wrong code cannot turn into a table scan.
    /// </summary>
    private const int MaxUserCodeScan = 1000;

    public async Task<DeviceCodeEntry?> FindByDeviceCodeAsync(Guid tenantId, string deviceCode, CancellationToken ct = default)
    {
        var lookup = protector.ProtectDeviceCode(deviceCode);

        var entry = await db.DeviceCodes
            .FirstOrDefaultAsync(dc => dc.TenantId == tenantId && dc.DeviceCodeHash == lookup, ct);
        if (entry is not null)
        {
            return entry;
        }

        // Legacy row: the code itself was stored. Those rows are short-lived, but a poll that is in flight during a
        // rolling deploy must still find its entry.
        return await db.DeviceCodes
            .FirstOrDefaultAsync(dc => dc.TenantId == tenantId && dc.DeviceCode != null && dc.DeviceCode == deviceCode, ct);
    }

    public async Task<DeviceCodeEntry?> FindByUserCodeAsync(Guid tenantId, string userCode, CancellationToken ct = default)
    {
        var lookup = protector.UserCodeLookup(userCode);
        if (lookup is not null)
        {
            var entry = await db.DeviceCodes
                .FirstOrDefaultAsync(dc => dc.TenantId == tenantId && dc.UserCodeProtected == lookup, ct);
            if (entry is not null)
            {
                return entry;
            }
        }

        // The user code cannot be looked up by its value (no keyed hash is configured) or the row was written before
        // the key was configured: compare it with the live device codes of the tenant.
        var candidates = await db.DeviceCodes
            .Where(dc => dc.TenantId == tenantId && dc.ExpiresAt > DateTimeOffset.UtcNow)
            .OrderByDescending(dc => dc.CreatedAt)
            .Take(MaxUserCodeScan)
            .ToListAsync(ct);

        if (candidates.Count == MaxUserCodeScan)
        {
            logger.LogWarning(
                "More than {Max} live device codes in the tenant: the user code scan is truncated. Configure Security:LookupHashKey so user codes are looked up by their hash.",
                MaxUserCodeScan);
        }

        foreach (var candidate in candidates)
        {
            if (protector.MatchesUserCode(candidate.UserCodeProtected, userCode) ||
                protector.MatchesUserCode(candidate.UserCode, userCode))
            {
                return candidate;
            }
        }

        return null;
    }
}
