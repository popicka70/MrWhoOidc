using System;
using System.Collections.Generic;
using System.Linq;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.WebAuth.Admin.Dto;

public sealed record InstallLicenseRequest(string LicenseKey, string? Notes);

public sealed record ValidateLicenseRequest(string LicenseKey);

public sealed record LicenseInfoDto(
    string Tier,
    string? OrganizationName,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    bool IsExpired,
    bool IsValid,
    int? DaysUntilExpiry,
    IReadOnlyCollection<string> EnabledFeatures,
    IReadOnlyDictionary<string, long> Limits,
    string Scope,
    string? IssuedTo,
    Guid? LicensedTenantId,
    string? LicensedTenantSlug);

public sealed record LicenseValidationResponseDto(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    LicenseInfoDto? License);

public sealed record FeatureUsageSummaryDto(
    string FeatureName,
    long UsageCount,
    DateTimeOffset FirstUsed,
    DateTimeOffset LastUsed);

public sealed record FeatureUsageReportDto(
    IReadOnlyList<FeatureUsageSummaryDto> Metrics,
    string AggregationPeriod,
    DateTimeOffset FromDate,
    DateTimeOffset ToDate);

public sealed record UsageLimitInfoDto(
    string Key,
    long CurrentUsage,
    long Limit,
    double Utilization,
    bool IsNearLimit,
    bool IsAtLimit);

public sealed record UsageLimitsReportDto(
    LicenseInfoDto License,
    IReadOnlyList<UsageLimitInfoDto> Limits);

public sealed record LicenseTierDescriptorDto(
    string TierKey,
    string DisplayName,
    string Description,
    IReadOnlyList<string> Features,
    IReadOnlyDictionary<string, long> DefaultLimits);

public sealed record FieldValidationErrorDto(string Field, string Message);

public sealed record LicenseValidationErrorDto(
    string Error,
    string ErrorDescription,
    IReadOnlyList<FieldValidationErrorDto> ValidationErrors);

public sealed record LicenseHistoryEntryDto(
    Guid Id,
    string Action,
    string? OldTier,
    string? NewTier,
    string? Notes,
    string? Reason,
    DateTimeOffset CreatedAt,
    Guid? CreatedBy,
    string? UserAgent,
    string? IpAddress);

public sealed record LicenseHistoryResponseDto(
    IReadOnlyList<LicenseHistoryEntryDto> Entries,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

internal static class LicenseDtoMapper
{
    public static LicenseInfoDto ToDto(LicenseInfo license, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(license);

        int? daysUntilExpiry = null;
        if (!license.IsExpired)
        {
            var remaining = license.ValidUntil - now;
            if (remaining > TimeSpan.Zero)
            {
                daysUntilExpiry = (int)Math.Ceiling(remaining.TotalDays);
            }
            else
            {
                daysUntilExpiry = 0;
            }
        }

        var enabledFeatures = license.EnabledFeatures
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var limits = new Dictionary<string, long>(license.Limits, StringComparer.OrdinalIgnoreCase);

        return new LicenseInfoDto(
            license.Tier,
            license.OrganizationName,
            license.ValidFrom,
            license.ValidUntil,
            license.IsExpired,
            license.IsValid,
            daysUntilExpiry,
            enabledFeatures,
            limits,
            license.Scope.ToString().ToLowerInvariant(),
            license.IssuedTo,
            license.LicensedTenantId,
            license.LicensedTenantSlug);
    }

    public static LicenseValidationResponseDto ToDto(LicenseValidationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        var licenseDto = result.LicenseInfo is null ? null : ToDto(result.LicenseInfo, now);
        return new LicenseValidationResponseDto(result.IsValid, result.ErrorCode, result.ErrorMessage, licenseDto);
    }

    public static LicenseValidationErrorDto ToErrorDto(LicenseValidationResult result)
    {
        var errorCode = string.IsNullOrWhiteSpace(result.ErrorCode) ? "validation_failed" : result.ErrorCode!;
        var errorDescription = string.IsNullOrWhiteSpace(result.ErrorMessage)
            ? "License validation failed"
            : result.ErrorMessage!;
        return new LicenseValidationErrorDto(errorCode, errorDescription, Array.Empty<FieldValidationErrorDto>());
    }

    public static LicenseHistoryEntryDto ToDto(LicenseHistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return new LicenseHistoryEntryDto(
            entry.Id,
            entry.Action,
            entry.OldTier,
            entry.NewTier,
            entry.Notes,
            entry.Reason,
            entry.CreatedAt,
            entry.CreatedBy,
            entry.UserAgent,
            entry.IpAddress);
    }

    public static LicenseHistoryResponseDto ToDto(PagedResult<LicenseHistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var entries = history.Items.Select(ToDto).ToList();
        return new LicenseHistoryResponseDto(entries, history.TotalCount, history.Page, history.PageSize, history.TotalPages);
    }

    public static FeatureUsageReportDto ToDto(FeatureUsageReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var metrics = report.Metrics
            .Select(m => new FeatureUsageSummaryDto(m.FeatureName, m.UsageCount, m.FirstUsed, m.LastUsed))
            .ToList();

        return new FeatureUsageReportDto(metrics, report.AggregationPeriod, report.FromDate, report.ToDate);
    }

    public static UsageLimitsReportDto ToDto(UsageLimitsReport report, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(report);
        var licenseDto = ToDto(report.License, now);
        var limits = report.Limits
            .Select(l => new UsageLimitInfoDto(l.LimitType, l.CurrentUsage, l.LimitValue, l.UtilizationPercentage, l.IsNearLimit, l.IsAtLimit))
            .ToList();
        return new UsageLimitsReportDto(licenseDto, limits);
    }

    public static IReadOnlyList<LicenseTierDescriptorDto> ToDto(IReadOnlyList<LicenseTierDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        return descriptors
            .Select(d => new LicenseTierDescriptorDto(d.TierKey, d.DisplayName, d.Description, d.Features, d.DefaultLimits))
            .ToList();
    }
}
