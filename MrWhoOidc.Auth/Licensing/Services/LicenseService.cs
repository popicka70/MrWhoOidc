using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Options;
using MrWhoOidc.Auth.Licensing.Repositories;
using MrWhoOidc.Auth.Licensing.Validators;
using MrWhoOidc.ServiceDefaults.Observability;

namespace MrWhoOidc.Auth.Licensing.Services;

internal sealed class LicenseService : ILicenseService
{
    private const string LicenseInstalledAction = "installed";
    private const string LicenseRevokedAction = "revoked";
    private const string LicenseUpdatedAction = "updated";

    private readonly ILicenseRepository _repository;
    private readonly ILicenseValidator _validator;
    private readonly IMemoryCache _cache;
    private readonly LicensingOptions _options;
    private readonly ILogger<LicenseService> _logger;
    private readonly TimeProvider _timeProvider;

    public LicenseService(
        ILicenseRepository repository,
        ILicenseValidator validator,
        IMemoryCache cache,
        IOptions<LicensingOptions> options,
        ILogger<LicenseService> logger,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<LicenseInfo?> GetCurrentLicenseAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
    {
        var cacheKey = BuildCacheKey(tenantId);
        if (_cache.TryGetValue(cacheKey, out LicenseInfo? cached) && cached is not null)
        {
            return cached;
        }

        var license = await _repository.GetActiveLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (license is null)
        {
            var defaultLicense = CreateDefaultLicenseInfo();
            if (defaultLicense is not null)
            {
                SetCache(cacheKey, defaultLicense);
            }

            return defaultLicense;
        }

        var parsed = await _validator.ParseLicenseAsync(license.LicenseKey, cancellationToken).ConfigureAwait(false);
        if (parsed is null)
        {
            _logger.LogWarning("Failed to parse stored license for tenant {Tenant}.", TenantScope(tenantId));
            return null;
        }

        var validation = await _validator.ValidateBusinessRulesAsync(parsed, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.LicenseInfo is null)
        {
            _logger.LogWarning(
                "Stored license failed business validation for tenant {Tenant}: {ErrorCode}",
                TenantScope(tenantId),
                validation.ErrorCode);
            return null;
        }

        SetCache(cacheKey, validation.LicenseInfo);
        return validation.LicenseInfo;
    }

    public async Task<LicenseValidationResult> InstallLicenseAsync(
        string licenseKey,
        Guid? tenantId = null,
        Guid? installedBy = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var success = false;

        try
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                _logger.LogWarning("License install rejected for {Tenant} due to empty key.", TenantScope(tenantId));
                var invalid = LicenseValidationResult.InvalidFormat();
                success = invalid.IsValid;
                return invalid;
            }

            var signatureResult = await _validator.ValidateSignatureAsync(licenseKey, cancellationToken).ConfigureAwait(false);
            if (!signatureResult.IsValid)
            {
                _logger.LogWarning("License install signature validation failed for {Tenant}: {ErrorCode}.", TenantScope(tenantId), signatureResult.ErrorCode);
                success = signatureResult.IsValid;
                return signatureResult;
            }

            var licenseInfo = signatureResult.LicenseInfo
                ?? await _validator.ParseLicenseAsync(licenseKey, cancellationToken).ConfigureAwait(false);
            if (licenseInfo is null)
            {
                _logger.LogWarning("License install parse failed for {Tenant}.", TenantScope(tenantId));
                var invalid = LicenseValidationResult.InvalidFormat();
                success = invalid.IsValid;
                return invalid;
            }

            var businessResult = await _validator.ValidateBusinessRulesAsync(licenseInfo, cancellationToken).ConfigureAwait(false);
            if (!businessResult.IsValid || businessResult.LicenseInfo is null)
            {
                _logger.LogWarning("License install business validation failed for {Tenant}: {ErrorCode}.", TenantScope(tenantId), businessResult.ErrorCode);
                success = businessResult.IsValid;
                return businessResult;
            }

            var now = _timeProvider.GetUtcNow();
            var existing = await _repository.GetActiveLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);

            if (existing is not null && !string.Equals(existing.LicenseKey, licenseKey, StringComparison.Ordinal))
            {
                existing.IsActive = false;
                existing.RevokedAt = now;
                existing.RevocationReason = "Replaced by new license";
                existing.UpdatedAt = now;
                existing.UpdatedBy = installedBy;
                await _repository.UpdateLicenseAsync(existing, cancellationToken).ConfigureAwait(false);

                await AddHistoryAsync(
                    existing.Id,
                    LicenseRevokedAction,
                    existing.LicenseKey,
                    null,
                    existing.Tier,
                    null,
                    existing.RevocationReason,
                    existing.RevocationReason,
                    installedBy,
                    null,
                    null,
                    now,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Deactivated previous license {LicenseId} for {Tenant}.", existing.Id, TenantScope(tenantId));
            }

            if (existing is not null && string.Equals(existing.LicenseKey, licenseKey, StringComparison.Ordinal))
            {
                existing.Tier = businessResult.LicenseInfo.Tier;
                existing.OrganizationName = businessResult.LicenseInfo.OrganizationName;
                existing.ValidFrom = businessResult.LicenseInfo.ValidFrom;
                existing.ValidUntil = businessResult.LicenseInfo.ValidUntil;
                existing.IsActive = true;
                existing.RevokedAt = null;
                existing.RevocationReason = null;
                existing.UpdatedAt = now;
                existing.UpdatedBy = installedBy;

                await _repository.UpdateLicenseAsync(existing, cancellationToken).ConfigureAwait(false);

                await AddHistoryAsync(
                    existing.Id,
                    LicenseUpdatedAction,
                    existing.LicenseKey,
                    existing.LicenseKey,
                    existing.Tier,
                    businessResult.LicenseInfo.Tier,
                    notes,
                    null,
                    installedBy,
                    null,
                    null,
                    now,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Refreshed existing license {LicenseId} for {Tenant} with tier {Tier}.", existing.Id, TenantScope(tenantId), existing.Tier);
            }
            else
            {
                var entity = new License
                {
                    TenantId = tenantId,
                    LicenseKey = licenseKey,
                    Tier = businessResult.LicenseInfo.Tier,
                    OrganizationName = businessResult.LicenseInfo.OrganizationName,
                    ValidFrom = businessResult.LicenseInfo.ValidFrom,
                    ValidUntil = businessResult.LicenseInfo.ValidUntil,
                    IsActive = true,
                    CreatedAt = now,
                    CreatedBy = installedBy,
                    UpdatedAt = null,
                    UpdatedBy = null,
                    RevokedAt = null,
                    RevocationReason = null
                };

                entity = await _repository.CreateLicenseAsync(entity, cancellationToken).ConfigureAwait(false);

                await AddHistoryAsync(
                    entity.Id,
                    LicenseInstalledAction,
                    existing?.LicenseKey,
                    entity.LicenseKey,
                    existing?.Tier,
                    entity.Tier,
                    notes,
                    null,
                    installedBy,
                    null,
                    null,
                    now,
                    cancellationToken).ConfigureAwait(false);

                _logger.LogInformation("Created new license {LicenseId} for {Tenant} with tier {Tier}.", entity.Id, TenantScope(tenantId), entity.Tier);
            }

            InvalidateCache(tenantId);

            var result = LicenseValidationResult.Success(businessResult.LicenseInfo);
            success = result.IsValid;
            _logger.LogInformation(
                "License install completed for {Tenant} with tier {Tier} (organization {Organization}).",
                TenantScope(tenantId),
                businessResult.LicenseInfo.Tier,
                businessResult.LicenseInfo.OrganizationName ?? "unknown");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error installing license for {Tenant}.", TenantScope(tenantId));
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LicensingMetrics.RecordInstallResult(success, elapsed);
        }
    }

    public async Task<LicenseValidationResult> ValidateLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
    {
        LicenseValidationResult result = LicenseValidationResult.InvalidFormat();

        try
        {
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                _logger.LogWarning("License validation rejected due to empty key.");
                result = LicenseValidationResult.InvalidFormat();
                return result;
            }

            var signatureResult = await _validator.ValidateSignatureAsync(licenseKey, cancellationToken).ConfigureAwait(false);
            if (!signatureResult.IsValid || signatureResult.LicenseInfo is null)
            {
                _logger.LogWarning("License validation signature check failed: {ErrorCode}.", signatureResult.ErrorCode);
                result = signatureResult;
                return result;
            }

            result = await _validator.ValidateBusinessRulesAsync(signatureResult.LicenseInfo, cancellationToken).ConfigureAwait(false);
            if (!result.IsValid)
            {
                _logger.LogWarning("License validation business rules failed: {ErrorCode}.", result.ErrorCode);
            }
            else if (result.LicenseInfo is not null)
            {
                _logger.LogInformation("License key validated successfully with tier {Tier} (organization {Organization}).", result.LicenseInfo.Tier, result.LicenseInfo.OrganizationName ?? "unknown");
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error validating license key.");
            result = LicenseValidationResult.InvalidFormat();
            throw;
        }
        finally
        {
            LicensingMetrics.RecordValidationResult(result.IsValid);
        }
    }

    public async Task<bool> RevokeLicenseAsync(
        string reason,
        Guid? tenantId = null,
        Guid? revokedBy = null,
        CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var success = false;

        try
        {
            var license = await _repository.GetActiveLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (license is null)
            {
                _logger.LogInformation("No active license found to revoke for {Tenant}.", TenantScope(tenantId));
                return false;
            }

            var now = _timeProvider.GetUtcNow();
            license.IsActive = false;
            license.RevokedAt = now;
            license.RevocationReason = reason;
            license.UpdatedAt = now;
            license.UpdatedBy = revokedBy;

            await _repository.UpdateLicenseAsync(license, cancellationToken).ConfigureAwait(false);

            await AddHistoryAsync(
                license.Id,
                LicenseRevokedAction,
                license.LicenseKey,
                null,
                license.Tier,
                null,
                reason,
                reason,
                revokedBy,
                null,
                null,
                now,
                cancellationToken).ConfigureAwait(false);

            InvalidateCache(tenantId);

            success = true;
            _logger.LogInformation(
                "License revoked for {Tenant} with reason '{Reason}' by {UserId}.",
                TenantScope(tenantId),
                reason,
                revokedBy);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error revoking license for {Tenant}.", TenantScope(tenantId));
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            LicensingMetrics.RecordRevokeResult(success, elapsed);
        }
    }

    public Task<PagedResult<LicenseHistoryEntry>> GetLicenseHistoryAsync(
        Guid? tenantId = null,
        int page = 1,
        int pageSize = 20,
        string? actionFilter = null,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetLicenseHistoryAsync(tenantId, page, pageSize, actionFilter, cancellationToken);
    }

    private async Task AddHistoryAsync(
        Guid licenseId,
        string action,
        string? oldLicenseKey,
        string? newLicenseKey,
        string? oldTier,
        string? newTier,
        string? notes,
        string? reason,
        Guid? createdBy,
        string? userAgent,
        string? ipAddress,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken)
    {
        var entry = new LicenseHistoryEntry
        {
            LicenseId = licenseId,
            Action = action,
            OldLicenseKey = oldLicenseKey,
            NewLicenseKey = newLicenseKey,
            OldTier = oldTier,
            NewTier = newTier,
            Notes = notes,
            Reason = reason,
            CreatedAt = timestamp,
            CreatedBy = createdBy,
            UserAgent = userAgent,
            IpAddress = ipAddress
        };

        await _repository.AddHistoryEntryAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private void SetCache(string cacheKey, LicenseInfo license)
    {
        var duration = GetCacheDuration();
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _cache.Set(cacheKey, license, duration);
    }

    private void InvalidateCache(Guid? tenantId)
    {
        var cacheKey = BuildCacheKey(tenantId);
        _cache.Remove(cacheKey);
    }

    private TimeSpan GetCacheDuration()
    {
        return _options.CacheExpirationMinutes <= 0
            ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromMinutes(_options.CacheExpirationMinutes);
    }

    private static string BuildCacheKey(Guid? tenantId) => tenantId.HasValue
        ? $"license:{tenantId.Value}"
        : "license:platform";

    private static string TenantScope(Guid? tenantId) => tenantId?.ToString() ?? "platform";

    private LicenseInfo? CreateDefaultLicenseInfo()
    {
        if (string.IsNullOrWhiteSpace(_options.DefaultTier))
        {
            return null;
        }

        try
        {
            _ = LicenseTierExtensions.FromTierString(_options.DefaultTier);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Configured default license tier '{Tier}' is invalid.", _options.DefaultTier);
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        return new LicenseInfo(
            _options.DefaultTier,
            null,
            now,
            now.AddYears(10),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            false,
            true);
    }
}
