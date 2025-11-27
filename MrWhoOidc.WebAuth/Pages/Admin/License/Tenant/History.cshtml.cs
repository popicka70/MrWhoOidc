using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Admin.Dto;

namespace MrWhoOidc.WebAuth.Pages.Admin.License.Tenant;

/// <summary>
/// Tenant license history page - shows audit trail of tenant license changes.
/// Always operates on the current tenant context.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class HistoryModel : LicensePageModelBase
{
    private const int MaxPageSize = 100;

    private readonly ILicenseService _licenseService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HistoryModel> _logger;

    public HistoryModel(
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IAuthorizationService authorizationService,
        AuthDbContext db,
        TimeProvider timeProvider,
        ILogger<HistoryModel> logger)
        : base(tenantAccessor, multiTenancyOptions, authorizationService, db)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Guid? TenantId { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 20;

    [BindProperty(SupportsGet = true)]
    public string? ActionFilter { get; set; }

    public IReadOnlyList<SelectListItem> ActionOptions { get; private set; } = Array.Empty<SelectListItem>();

    public LicenseHistoryResponseDto? History { get; private set; }

    public string? TenantName { get; private set; }

    public bool HasEntries => History is { Entries.Count: > 0 };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Max(1, PageNumber);
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize);
        var normalizedAction = NormalizeActionFilter(ActionFilter);
        var filterValue = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;

        // Use current tenant context
        var currentTenant = TenantAccessor.CurrentTenant;
        TenantId = currentTenant?.TenantId;
        TenantName = currentTenant?.Name ?? "Current tenant";
        ActionOptions = BuildActionFilterOptions(normalizedAction);
        ActionFilter = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;

        if (TenantId is null)
        {
            ModelState.AddModelError(string.Empty, "No tenant context available.");
            return Page();
        }

        try
        {
            var history = await _licenseService.GetLicenseHistoryAsync(
                TenantId,
                PageNumber,
                PageSize,
                filterValue,
                cancellationToken).ConfigureAwait(false);

            History = LicenseDtoMapper.ToDto(history);
            PageNumber = history.Page;
            PageSize = history.PageSize;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load tenant license history for tenant {TenantId}", TenantId);
            ModelState.AddModelError(string.Empty, "Unable to load license history. Please try again.");
        }

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    public string FormatRelativeTime(DateTimeOffset value) => FormatRelative(value, _timeProvider.GetUtcNow());

    public string DescribeAction(string action) => FormatActionDisplay(action);

    public string DescribeTier(string? tier) => string.IsNullOrWhiteSpace(tier) ? "—" : GetTierDisplayName(tier);
}
