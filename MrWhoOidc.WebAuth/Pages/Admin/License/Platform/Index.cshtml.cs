using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Admin.Dto;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Pages.Admin.License.Platform;

/// <summary>
/// Platform license index page - shows current platform license status and analytics.
/// Requires platform-admin role.
/// </summary>
[Authorize(Policy = "platform-admin")]
public class IndexModel : LicensePageModelBase
{
    private readonly ILicenseService _licenseService;
    private readonly ILicenseAnalyticsService _analyticsService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ILicenseService licenseService,
        ILicenseAnalyticsService analyticsService,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IAuthorizationService authorizationService,
        AuthDbContext db,
        TimeProvider timeProvider,
        ILogger<IndexModel> logger)
        : base(tenantAccessor, multiTenancyOptions, authorizationService, db)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _analyticsService = analyticsService ?? throw new ArgumentNullException(nameof(analyticsService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LicenseSummaryModel? Summary { get; private set; }

    public FeatureUsageReportModel? UsageReport { get; private set; }

    public IReadOnlyList<UsageLimitStatusModel> UsageLimits { get; private set; } = Array.Empty<UsageLimitStatusModel>();

    public IReadOnlyList<LicenseTierDisplayModel> TierCatalog { get; private set; } = Array.Empty<LicenseTierDisplayModel>();

    public string? InfoMessage { get; private set; }

    public string? UsageMessage { get; private set; }

    public string? LimitsMessage { get; private set; }

    public string? TierMessage { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Platform license uses null tenantId
            var info = await _licenseService.GetCurrentLicenseAsync(null, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                InfoMessage = "No platform license installed. Install a platform license to enable enterprise features.";
                await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);
                return Page();
            }

            var now = _timeProvider.GetUtcNow();
            var dto = LicenseDtoMapper.ToDto(info, now);
            Summary = BuildSummary(dto, now);

            await LoadAnalyticsAsync(now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load platform license details");
            InfoMessage = "Unable to load platform license information. Please try again.";
            await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);
        }

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    public string FormatRelativeTime(DateTimeOffset value) => FormatRelative(value, _timeProvider.GetUtcNow());

    private async Task LoadAnalyticsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Platform-level usage (null tenantId)
            var usage = await _analyticsService.GetFeatureUsageAsync(null, null, null, null, cancellationToken).ConfigureAwait(false);
            if (usage.Metrics.Count > 0)
            {
                UsageReport = BuildFeatureUsageReport(usage, now);
            }
            else
            {
                UsageMessage = "No feature usage recorded for the platform.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load platform feature usage analytics");
            UsageMessage = "Feature usage analytics are temporarily unavailable.";
        }

        try
        {
            var limits = await _analyticsService.GetUsageLimitsAsync(null, cancellationToken).ConfigureAwait(false);
            UsageLimits = BuildUsageLimitStatus(limits);
            if (UsageLimits.Count == 0)
            {
                LimitsMessage = "No limit data available for the platform license.";
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "No license scope while loading platform limit analytics");
            LimitsMessage = "No license data available to calculate limits.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load platform license limit analytics");
            LimitsMessage = "Limit analytics are temporarily unavailable.";
        }
    }

    private async Task LoadTierCatalogAsync(CancellationToken cancellationToken)
    {
        if (TierCatalog.Count > 0)
        {
            return;
        }

        try
        {
            var tiers = await _analyticsService.GetLicenseTiersAsync(cancellationToken).ConfigureAwait(false);
            TierCatalog = BuildTierDisplay(tiers);
            if (TierCatalog.Count == 0)
            {
                TierMessage = "No tier reference data available.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load tier catalog for platform license dashboard.");
            TierMessage = "Tier catalog is temporarily unavailable.";
        }
    }
}
