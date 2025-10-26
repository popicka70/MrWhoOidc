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

namespace MrWhoOidc.WebAuth.Pages.Admin.License;

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

    [BindProperty(SupportsGet = true)]
    public string Scope { get; set; } = LicenseTenantScope.Tenant;

    [BindProperty(SupportsGet = true)]
    public Guid? TenantId { get; set; }

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageSize { get; set; } = 20;

    [BindProperty(SupportsGet = true)]
    public string? ActionFilter { get; set; }

    public IReadOnlyList<SelectListItem> ScopeOptions { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> TenantOptions { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> ActionOptions { get; private set; } = Array.Empty<SelectListItem>();

    public LicenseHistoryResponseDto? History { get; private set; }

    public bool IsPlatformAdmin { get; private set; }

    public string? TenantName { get; private set; }

    public string TenantScopeTenant => LicenseTenantScope.Tenant;

    public string TenantScopePlatform => LicenseTenantScope.Platform;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        PageNumber = Math.Max(1, PageNumber);
        PageSize = Math.Clamp(PageSize, 1, MaxPageSize);
        var normalizedAction = NormalizeActionFilter(ActionFilter);
        var filterValue = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;

        var context = await ResolveTenantContextAsync(Scope, TenantId, cancellationToken).ConfigureAwait(false);
        if (context.ErrorResult is not null)
        {
            return context.ErrorResult;
        }

        ApplyContext(context, normalizedAction);

        try
        {
            var history = await _licenseService.GetLicenseHistoryAsync(
                context.TenantId,
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
            _logger.LogError(ex, "Failed to load license history for scope {Scope} and tenant {TenantId}", context.Scope, context.TenantId);
            ModelState.AddModelError(string.Empty, "Unable to load license history. Please try again.");
        }

        return Page();
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

    public string FormatRelativeTime(DateTimeOffset value) => FormatRelative(value, _timeProvider.GetUtcNow());

    public string DescribeAction(string action) => FormatActionDisplay(action);

    public string DescribeTier(string? tier) => string.IsNullOrWhiteSpace(tier) ? "—" : GetTierDisplayName(tier);

    public bool HasEntries => History is { Entries.Count: > 0 };

    private void ApplyContext(LicenseTenantContext context, string normalizedAction)
    {
        Scope = context.Scope;
        TenantId = context.TenantId;
        TenantOptions = context.TenantOptions;
        IsPlatformAdmin = context.IsPlatformAdmin;
        TenantName = context.Scope == LicenseTenantScope.Platform ? "Platform license" : context.TenantName;
        ScopeOptions = BuildScopeOptions(context);
        ActionOptions = BuildActionFilterOptions(normalizedAction);
        ActionFilter = string.IsNullOrEmpty(normalizedAction) ? null : normalizedAction;
    }
}
