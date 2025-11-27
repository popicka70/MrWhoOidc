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

namespace MrWhoOidc.WebAuth.Pages.Admin.License.Platform;

/// <summary>
/// Platform license history page - shows audit trail of platform license changes.
/// Requires platform-admin role.
/// </summary>
[Authorize(Policy = "platform-admin")]
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

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 20;

    [BindProperty(SupportsGet = true)]
    public string? ActionFilter { get; set; }

    public IReadOnlyList<SelectListItem> ActionOptions { get; private set; } = Array.Empty<SelectListItem>();

    public LicenseHistoryResponseDto? History { get; private set; }

    public bool HasEntries => History is { Entries.Count: > 0 };

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Max(1, PageNumber);
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize);
        var normalizedAction = NormalizeActionFilter(ActionFilter);
        var filterValue = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;

        ActionOptions = BuildActionFilterOptions(normalizedAction);
        ActionFilter = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;

        try
        {
            // Platform license history uses null tenantId
            var history = await _licenseService.GetLicenseHistoryAsync(
                null,
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
            _logger.LogError(ex, "Failed to load platform license history");
            ModelState.AddModelError(string.Empty, "Unable to load platform license history. Please try again.");
        }

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    public string FormatRelativeTime(DateTimeOffset value) => FormatRelative(value, _timeProvider.GetUtcNow());

    public string DescribeAction(string action) => FormatActionDisplay(action);

    public string DescribeTier(string? tier) => string.IsNullOrWhiteSpace(tier) ? "—" : GetTierDisplayName(tier);
}
