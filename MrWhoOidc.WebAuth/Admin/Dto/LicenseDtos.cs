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
    IReadOnlyDictionary<string, long> Limits);

public sealed record LicenseValidationResponseDto(
    bool IsValid,
    string? ErrorCode,
    string? ErrorMessage,
    LicenseInfoDto? License);

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
            limits);
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
}
