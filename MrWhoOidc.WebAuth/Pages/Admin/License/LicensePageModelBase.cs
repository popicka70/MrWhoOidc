using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.WebAuth.Admin.Dto;

namespace MrWhoOidc.WebAuth.Pages.Admin.License;

public abstract class LicensePageModelBase : TenantAwarePageModel
{
    private static readonly Dictionary<string, string> FeatureDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["basic_oidc"] = "Basic OIDC",
        ["basic_admin_ui"] = "Admin UI",
        ["multi_tenancy"] = "Multi-tenancy",
        ["advanced_security"] = "Advanced security",
        ["client_secret_rotation"] = "Client secret rotation",
        ["enhanced_audit_logging"] = "Enhanced audit logging",
        ["unlimited_scale"] = "Unlimited scale",
        ["dpop"] = "DPoP",
        ["token_exchange"] = "Token exchange",
        ["backchannel_logout"] = "Back-channel logout",
        ["ldap_integration"] = "LDAP/AD integration",
        ["custom_claim_mappings"] = "Custom claim mappings",
        ["advanced_monitoring"] = "Advanced monitoring",
        ["webauthn"] = "WebAuthn",
        ["risk_based_auth"] = "Risk-based authentication",
        ["hsm_integration"] = "HSM integration",
        ["professional_services"] = "Professional services"
    };

    private static readonly Dictionary<string, string> LimitDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["users"] = "User capacity",
        ["tenants"] = "Tenant capacity",
        ["clients"] = "Client capacity",
        ["client_secrets"] = "Active client secrets",
        ["api_calls_per_hour"] = "API calls per hour"
    };

    private static readonly Dictionary<string, string> TierDisplayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["community"] = "Community",
        ["professional"] = "Professional",
        ["enterprise"] = "Enterprise",
        ["enterprise+"] = "Enterprise+"
    };

    private static readonly string[] KnownActions = ["installed", "updated", "expired", "revoked", "validated"];

    private readonly IAuthorizationService _authorizationService;
    private readonly AuthDbContext _db;

    protected LicensePageModelBase(
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IAuthorizationService authorizationService,
        AuthDbContext db)
        : base(tenantAccessor, multiTenancyOptions)
    {
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    protected async Task<LicenseTenantContext> ResolveTenantContextAsync(string scope, Guid? requestedTenantId, CancellationToken cancellationToken)
    {
        var platformAdminResult = await _authorizationService.AuthorizeAsync(User, null, "platform-admin").ConfigureAwait(false);
        var isPlatformAdmin = platformAdminResult.Succeeded;
        var normalizedScope = isPlatformAdmin && string.Equals(scope, LicenseTenantScope.Platform, StringComparison.OrdinalIgnoreCase)
            ? LicenseTenantScope.Platform
            : LicenseTenantScope.Tenant;

        var tenantRecords = new List<(Guid Id, string Name)>();
        if (isPlatformAdmin)
        {
            var records = await _db.Tenants.AsNoTracking()
                .OrderBy(t => t.Name)
                .Select(t => new { t.Id, t.Name })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            tenantRecords = records.Select(t => (t.Id, t.Name)).ToList();
        }

        var tenantOptions = tenantRecords
            .Select(t => new SelectListItem(t.Name, t.Id.ToString()))
            .ToList();

        Guid? effectiveTenantId = null;
        string? tenantName = null;
        IActionResult? errorResult = null;

        if (normalizedScope == LicenseTenantScope.Platform)
        {
            if (!isPlatformAdmin)
            {
                errorResult = Forbid();
            }
            else
            {
                tenantName = "Platform license";
            }
        }
        else
        {
            if (isPlatformAdmin)
            {
                Guid? candidateId = requestedTenantId;
                if (!candidateId.HasValue)
                {
                    var currentTenant = TenantAccessor.CurrentTenant;
                    if (currentTenant is not null)
                    {
                        candidateId = currentTenant.TenantId;
                    }
                    else if (tenantRecords.Count > 0)
                    {
                        candidateId = tenantRecords[0].Id;
                    }
                }

                if (!candidateId.HasValue)
                {
                    errorResult = NotFound();
                }
                else
                {
                    var match = tenantRecords.FirstOrDefault(t => t.Id == candidateId.Value);
                    if (match == default)
                    {
                        errorResult = NotFound();
                    }
                    else
                    {
                        effectiveTenantId = match.Id;
                        tenantName = match.Name;
                        foreach (var option in tenantOptions)
                        {
                            option.Selected = Guid.TryParse(option.Value, out var optionId) && optionId == effectiveTenantId;
                        }
                    }
                }
            }
            else
            {
                var currentTenant = TenantAccessor.CurrentTenant;
                if (currentTenant is null)
                {
                    errorResult = Forbid();
                }
                else if (requestedTenantId.HasValue && requestedTenantId.Value != currentTenant.TenantId)
                {
                    errorResult = Forbid();
                }
                else
                {
                    effectiveTenantId = currentTenant.TenantId;
                    tenantName = currentTenant.Name;
                }
            }
        }

        return new LicenseTenantContext(isPlatformAdmin, normalizedScope, effectiveTenantId, tenantName, tenantOptions, errorResult);
    }

    protected List<SelectListItem> BuildScopeOptions(LicenseTenantContext context)
    {
        var items = new List<SelectListItem>
        {
            new("Tenant license", LicenseTenantScope.Tenant, context.Scope == LicenseTenantScope.Tenant)
        };

        if (context.IsPlatformAdmin)
        {
            items.Add(new SelectListItem("Platform license", LicenseTenantScope.Platform, context.Scope == LicenseTenantScope.Platform));
        }

        return items;
    }

    protected IReadOnlyList<SelectListItem> BuildActionFilterOptions(string selectedAction)
    {
        var items = new List<SelectListItem>
        {
            new("All actions", string.Empty, string.IsNullOrEmpty(selectedAction))
        };

        foreach (var action in KnownActions)
        {
            items.Add(new SelectListItem(FormatActionDisplay(action), action, string.Equals(action, selectedAction, StringComparison.Ordinal)));
        }

        return items;
    }

    protected LicenseSummaryModel BuildSummary(LicenseInfoDto info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);

        var (statusText, statusClass, isExpiringSoon) = GetStatusMetadata(info);
        var expiryText = BuildExpiryText(info, now);

        var features = info.EnabledFeatures
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new FeatureDisplay(f, GetFeatureDisplayName(f)))
            .ToList();

        var limits = info.Limits
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new LimitDisplay(
                kv.Key,
                GetLimitDisplayName(kv.Key),
                FormatLimitValue(kv.Value),
                kv.Value == -1,
                kv.Value == 0))
            .ToList();

        var scopeDisplay = FormatScopeDisplay(info);
        var issuedToDisplay = FormatIssuedToDisplay(info);
        var deploymentModeDisplay = FormatDeploymentModeDisplay(info);

        return new LicenseSummaryModel(
            info,
            GetTierDisplayName(info.Tier),
            statusText,
            statusClass,
            expiryText,
            isExpiringSoon,
            features,
            limits,
            scopeDisplay,
            issuedToDisplay,
            deploymentModeDisplay,
            info.IsSublicense,
            info.ParentLicenseId);
    }

    protected FeatureUsageReportModel BuildFeatureUsageReport(FeatureUsageReport report, DateTimeOffset reference)
    {
        ArgumentNullException.ThrowIfNull(report);

        var rows = report.Metrics
            .Select(metric => new FeatureUsageRow(
                metric.FeatureName,
                GetFeatureDisplayName(metric.FeatureName),
                metric.UsageCount,
                metric.FirstUsed,
                metric.LastUsed,
                FormatRelative(metric.FirstUsed, reference),
                FormatRelative(metric.LastUsed, reference)))
            .ToList();

        return new FeatureUsageReportModel(report.FromDate, report.ToDate, rows);
    }

    protected IReadOnlyList<UsageLimitStatusModel> BuildUsageLimitStatus(UsageLimitsReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Limits
            .Select(limit => new UsageLimitStatusModel(
                limit.LimitType,
                GetLimitDisplayName(limit.LimitType),
                limit.CurrentUsage,
                limit.LimitValue,
                FormatLimitValue(limit.LimitValue),
                limit.UtilizationPercentage,
                limit.IsNearLimit,
                limit.IsAtLimit))
            .ToList();
    }

    protected IReadOnlyList<LicenseTierDisplayModel> BuildTierDisplay(IReadOnlyList<LicenseTierDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);

        return descriptors
            .Select(tier => new LicenseTierDisplayModel(
                tier.TierKey,
                tier.DisplayName,
                tier.Description,
                tier.Features.Select(GetFeatureDisplayName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                tier.DefaultLimits
                    .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => new LimitDisplay(kv.Key, GetLimitDisplayName(kv.Key), FormatLimitValue(kv.Value), kv.Value == -1, kv.Value == 0))
                    .ToList()))
            .ToList();
    }

    protected static string NormalizeActionFilter(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return string.Empty;
        }

        var normalized = action.Trim().ToLowerInvariant();
        return KnownActions.Contains(normalized, StringComparer.Ordinal) ? normalized : string.Empty;
    }

    protected static string FormatActionDisplay(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return string.Empty;
        }

        return action switch
        {
            "installed" => "Installed",
            "updated" => "Updated",
            "expired" => "Expired",
            "revoked" => "Revoked",
            "validated" => "Validated",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(action.Replace('_', ' '))
        };
    }

    protected static string FormatRelative(DateTimeOffset timestamp, DateTimeOffset reference)
    {
        var delta = reference - timestamp;
        var suffix = delta >= TimeSpan.Zero ? "ago" : "from now";
        var span = delta.Duration();

        string value;
        if (span.TotalDays >= 1)
        {
            value = $"{(int)Math.Round(span.TotalDays)} day(s)";
        }
        else if (span.TotalHours >= 1)
        {
            value = $"{(int)Math.Round(span.TotalHours)} hour(s)";
        }
        else if (span.TotalMinutes >= 1)
        {
            value = $"{(int)Math.Round(span.TotalMinutes)} minute(s)";
        }
        else
        {
            value = $"{(int)Math.Max(1, Math.Round(span.TotalSeconds))} second(s)";
        }

        return $"{value} {suffix}";
    }

    protected static string FormatDateTime(DateTimeOffset value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);
    }

    private static (string Text, string CssClass, bool IsExpiringSoon) GetStatusMetadata(LicenseInfoDto info)
    {
        if (!info.IsValid)
        {
            return ("Invalid", "text-bg-danger", false);
        }

        if (info.IsExpired)
        {
            return ("Expired", "text-bg-danger", false);
        }

        if (info.DaysUntilExpiry is > 0 and <= 14)
        {
            return ("Expiring soon", "text-bg-warning", true);
        }

        return ("Active", "text-bg-success", false);
    }

    private static string BuildExpiryText(LicenseInfoDto info, DateTimeOffset now)
    {
        var local = info.ValidUntil.ToLocalTime();
        var formatted = local.ToString("f", CultureInfo.InvariantCulture);

        if (info.IsExpired)
        {
            return $"Expired on {formatted}";
        }

        if (info.DaysUntilExpiry is null)
        {
            return $"Valid until {formatted}";
        }

        return info.DaysUntilExpiry switch
        {
            > 1 => $"Valid until {formatted} ({info.DaysUntilExpiry} days remaining)",
            1 => $"Valid until {formatted} (1 day remaining)",
            0 => $"Valid until {formatted} (expires today)",
            _ => $"Valid until {formatted}"
        };
    }

    private static string FormatScopeDisplay(LicenseInfoDto info)
    {
        return string.Equals(info.Scope, LicenseTenantScope.Platform, StringComparison.OrdinalIgnoreCase)
            ? "Platform license"
            : "Tenant license";
    }

    private static string FormatIssuedToDisplay(LicenseInfoDto info)
    {
        if (!string.IsNullOrWhiteSpace(info.IssuedTo))
        {
            return info.IssuedTo!;
        }

        if (!string.IsNullOrWhiteSpace(info.LicensedTenantSlug))
        {
            return $"Tenant slug: {info.LicensedTenantSlug}";
        }

        if (info.LicensedTenantId.HasValue)
        {
            return $"Tenant ID: {info.LicensedTenantId.Value}";
        }

        return string.Equals(info.Scope, LicenseTenantScope.Platform, StringComparison.OrdinalIgnoreCase)
            ? "Platform (all tenants)"
            : "Tenant-specific";
    }

    private static string FormatDeploymentModeDisplay(LicenseInfoDto info)
    {
        return string.Equals(info.DeploymentMode, "singletenant", StringComparison.OrdinalIgnoreCase)
            ? "Single-tenant"
            : "Multi-tenant";
    }

    private static string GetFeatureDisplayName(string feature)
    {
        if (FeatureDisplayNames.TryGetValue(feature, out var name))
        {
            return name;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(feature.Replace('_', ' '));
    }

    private static string GetLimitDisplayName(string key)
    {
        if (LimitDisplayNames.TryGetValue(key, out var name))
        {
            return name;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key.Replace('_', ' '));
    }

    protected static string GetTierDisplayName(string tier)
    {
        if (TierDisplayNames.TryGetValue(tier, out var name))
        {
            return name;
        }

        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tier.Replace('_', ' '));
    }

    private static string FormatLimitValue(long value)
    {
        return value switch
        {
            -1 => "Unlimited",
            0 => "Disabled",
            _ => value.ToString("N0", CultureInfo.InvariantCulture)
        };
    }

    protected static class LicenseTenantScope
    {
        public const string Tenant = "tenant";
        public const string Platform = "platform";
    }

    protected sealed record LicenseTenantContext(
        bool IsPlatformAdmin,
        string Scope,
        Guid? TenantId,
        string? TenantName,
        List<SelectListItem> TenantOptions,
        IActionResult? ErrorResult);

    public sealed record FeatureDisplay(string Key, string DisplayName);

    public sealed record LimitDisplay(string Key, string DisplayName, string ValueDisplay, bool IsUnlimited, bool IsDisabled);

    public sealed record LicenseSummaryModel(
        LicenseInfoDto Info,
        string TierDisplay,
        string StatusText,
        string StatusCssClass,
        string ExpiryText,
        bool IsExpiringSoon,
        IReadOnlyList<FeatureDisplay> Features,
        IReadOnlyList<LimitDisplay> Limits,
        string ScopeDisplay,
        string IssuedToDisplay,
        string DeploymentModeDisplay,
        bool IsSublicense,
        string? ParentLicenseId);

    public sealed record FeatureUsageRow(
        string Key,
        string DisplayName,
        long UsageCount,
        DateTimeOffset FirstUsed,
        DateTimeOffset LastUsed,
        string FirstUsedRelative,
        string LastUsedRelative);

    public sealed record FeatureUsageReportModel(
        DateTimeOffset FromDate,
        DateTimeOffset ToDate,
        IReadOnlyList<FeatureUsageRow> Rows);

    public sealed record UsageLimitStatusModel(
        string Key,
        string DisplayName,
        long CurrentUsage,
        long LimitValue,
        string LimitDisplay,
        double Utilization,
        bool IsNearLimit,
        bool IsAtLimit);

    public sealed record LicenseTierDisplayModel(
        string TierKey,
        string DisplayName,
        string Description,
        IReadOnlyList<string> Features,
        IReadOnlyList<LimitDisplay> DefaultLimits);
}
