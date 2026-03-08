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

namespace MrWhoOidc.WebAuth.Pages.Admin.License;

/// <summary>
/// License management overview page - serves as a landing page with links to
/// platform and tenant license management pages.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class IndexModel : LicensePageModelBase
{
    private readonly ILicenseService _licenseService;
    private readonly ILicenseAnalyticsService _analyticsService;
    private readonly IAuthorizationService _authorizationService;
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
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LicenseSummaryModel? Summary { get; private set; }

    public IReadOnlyList<LicenseTierDisplayModel> TierCatalog { get; private set; } = Array.Empty<LicenseTierDisplayModel>();

    public bool IsPlatformAdmin { get; private set; }

    public string? InfoMessage { get; private set; }

    public string? TierMessage { get; private set; }

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        // Check if user is platform admin
        var platformAdminResult = await _authorizationService.AuthorizeAsync(User, null, "platform-admin").ConfigureAwait(false);
        IsPlatformAdmin = platformAdminResult.Succeeded;

        // Try to load current license for summary display
        try
        {
            var currentTenant = TenantAccessor.CurrentTenant;
            var tenantId = currentTenant?.TenantId;

            var info = await _licenseService.GetCurrentLicenseAsync(tenantId, cancellationToken).ConfigureAwait(false);
            if (info is not null)
            {
                var now = _timeProvider.GetUtcNow();
                var dto = LicenseDtoMapper.ToDto(info, now);
                Summary = BuildSummary(dto, now);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not load license summary for overview page");
            // Don't show error - this is just supplementary info
        }

        // Load tier catalog
        await LoadTierCatalogAsync(cancellationToken).ConfigureAwait(false);

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    private async Task LoadTierCatalogAsync(CancellationToken cancellationToken)
    {
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
            _logger.LogWarning(ex, "Failed to load tier catalog for license overview.");
            TierMessage = "Tier catalog is temporarily unavailable.";
        }
    }
}
