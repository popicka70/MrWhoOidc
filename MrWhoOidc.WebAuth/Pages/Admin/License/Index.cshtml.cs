using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Admin.Dto;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Pages.Admin.License;

[Authorize(Policy = "tenant-admin")]
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

    [BindProperty(SupportsGet = true)]
    public string Scope { get; set; } = LicenseTenantScope.Tenant;

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    public LicenseSummaryModel? Summary { get; private set; }

    public FeatureUsageReportModel? UsageReport { get; private set; }

    public IReadOnlyList<UsageLimitStatusModel> UsageLimits { get; private set; } = Array.Empty<UsageLimitStatusModel>();

    public IReadOnlyList<LicenseTierDisplayModel> TierCatalog { get; private set; } = Array.Empty<LicenseTierDisplayModel>();

    public IReadOnlyList<SelectListItem> ScopeOptions { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> TenantOptions { get; private set; } = Array.Empty<SelectListItem>();

    public string? TenantName { get; private set; }

    public bool IsPlatformAdmin { get; private set; }

    public string? InfoMessage { get; private set; }

    public string? UsageMessage { get; private set; }

    public string? LimitsMessage { get; private set; }

    public string? TierMessage { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public string TenantScopeTenant => LicenseTenantScope.Tenant;

    public string TenantScopePlatform => LicenseTenantScope.Platform;

    public bool IsViewingPlatform => string.Equals(Scope, LicenseTenantScope.Platform, StringComparison.OrdinalIgnoreCase);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var context = await ResolveTenantContextAsync(Scope, TenantId, cancellationToken).ConfigureAwait(false);
        if (context.ErrorResult is not null)
        {
            return context.ErrorResult;
        }

        ApplyContext(context);

        if (context.Scope == LicenseTenantScope.Tenant && context.TenantId is null)
        {
            InfoMessage = "Select a tenant to view license information.";
            return Page();
        }

        try
        {
            var info = await _licenseService.GetCurrentLicenseAsync(context.TenantId, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                InfoMessage = "No license installed.";
                await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);
                return Page();
            }

            var now = _timeProvider.GetUtcNow();
            var dto = LicenseDtoMapper.ToDto(info, now);
            Summary = BuildSummary(dto, now);

            await LoadAnalyticsAsync(context.TenantId, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load license details for scope {Scope} and tenant {TenantId}",
                context.Scope,
                context.TenantId);
            InfoMessage = "Unable to load license information. Please try again.";
            await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);
        }

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    public string FormatRelativeTime(DateTimeOffset value) => FormatRelative(value, _timeProvider.GetUtcNow());

    private void ApplyContext(LicenseTenantContext context)
    {
        Scope = context.Scope;
        TenantId = context.TenantId;
        TenantOptions = context.TenantOptions;
        IsPlatformAdmin = context.IsPlatformAdmin;
        TenantName = context.Scope == LicenseTenantScope.Platform ? "Platform license" : context.TenantName;
        ScopeOptions = BuildScopeOptions(context);
    }

    private async Task LoadAnalyticsAsync(Guid? tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var usage = await _analyticsService.GetFeatureUsageAsync(tenantId, null, null, null, cancellationToken).ConfigureAwait(false);
            if (usage.Metrics.Count > 0)
            {
                UsageReport = BuildFeatureUsageReport(usage, now);
            }
            else
            {
                UsageMessage = "No feature usage recorded for the selected period.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load feature usage analytics for tenant {TenantId}", tenantId);
            UsageMessage = "Feature usage analytics are temporarily unavailable.";
        }

        try
        {
            var limits = await _analyticsService.GetUsageLimitsAsync(tenantId, cancellationToken).ConfigureAwait(false);
            UsageLimits = BuildUsageLimitStatus(limits);
            if (UsageLimits.Count == 0)
            {
                LimitsMessage = "No limit data available for this license.";
            }
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "No license scope while loading limit analytics for tenant {TenantId}", tenantId);
            LimitsMessage = "No license data available to calculate limits.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load license limit analytics for tenant {TenantId}", tenantId);
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
            _logger.LogWarning(ex, "Failed to load tier catalog for analytics dashboard.");
            TierMessage = "Tier catalog is temporarily unavailable.";
        }
    }
}
