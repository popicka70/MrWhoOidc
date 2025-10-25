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
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IAuthorizationService authorizationService,
        AuthDbContext db,
        TimeProvider timeProvider,
        ILogger<IndexModel> logger)
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

    public LicenseSummaryModel? Summary { get; private set; }

    public IReadOnlyList<SelectListItem> ScopeOptions { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> TenantOptions { get; private set; } = Array.Empty<SelectListItem>();

    public string? TenantName { get; private set; }

    public bool IsPlatformAdmin { get; private set; }

    public string? InfoMessage { get; private set; }

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
                return Page();
            }

            var now = _timeProvider.GetUtcNow();
            var dto = LicenseDtoMapper.ToDto(info, now);
            Summary = BuildSummary(dto, now);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load license details for scope {Scope} and tenant {TenantId}",
                context.Scope,
                context.TenantId);
            InfoMessage = "Unable to load license information. Please try again.";
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
}
