using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
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
public class InstallModel : LicensePageModelBase
{
    private readonly ILicenseService _licenseService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InstallModel> _logger;

    public InstallModel(
        ILicenseService licenseService,
        ITenantAccessor tenantAccessor,
        IMultiTenancyOptions multiTenancyOptions,
        IAuthorizationService authorizationService,
        AuthDbContext db,
        TimeProvider timeProvider,
        ILogger<InstallModel> logger)
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

    [BindProperty]
    public InstallInput Input { get; set; } = new();

    public IReadOnlyList<SelectListItem> ScopeOptions { get; private set; } = Array.Empty<SelectListItem>();

    public IReadOnlyList<SelectListItem> TenantOptions { get; private set; } = Array.Empty<SelectListItem>();

    public string? TenantName { get; private set; }

    public bool IsPlatformAdmin { get; private set; }

    public LicenseValidationResponseDto? ValidationPreview { get; private set; }

    public LicenseValidationErrorDto? ValidationError { get; private set; }

    public LicenseSummaryModel? ValidationSummary { get; private set; }

    public string TenantScopeTenant => LicenseTenantScope.Tenant;

    public string TenantScopePlatform => LicenseTenantScope.Platform;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var context = await ResolveTenantContextAsync(Scope, TenantId, cancellationToken).ConfigureAwait(false);
        if (context.ErrorResult is not null)
        {
            return context.ErrorResult;
        }

        ApplyContext(context);
        return Page();
    }

    public async Task<IActionResult> OnPostValidateAsync(CancellationToken cancellationToken)
    {
        var context = await ResolveTenantContextAsync(Scope, TenantId, cancellationToken).ConfigureAwait(false);
        if (context.ErrorResult is not null)
        {
            return context.ErrorResult;
        }

        ApplyContext(context);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var cleanedKey = Input.LicenseKey?.Trim() ?? string.Empty;
            var result = await _licenseService.ValidateLicenseKeyAsync(cleanedKey, cancellationToken).ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            ValidationPreview = LicenseDtoMapper.ToDto(result, now);
            if (ValidationPreview.License is not null)
            {
                ValidationSummary = BuildSummary(ValidationPreview.License, now);
            }

            if (!result.IsValid)
            {
                ValidationError = LicenseDtoMapper.ToErrorDto(result);
                ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate license key for scope {Scope} and tenant {TenantId}", context.Scope, context.TenantId);
            ModelState.AddModelError(string.Empty, "Failed to validate the license key. Please try again.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostInstallAsync(CancellationToken cancellationToken)
    {
        var context = await ResolveTenantContextAsync(Scope, TenantId, cancellationToken).ConfigureAwait(false);
        if (context.ErrorResult is not null)
        {
            return context.ErrorResult;
        }

        ApplyContext(context);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var cleanedKey = Input.LicenseKey?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cleanedKey))
            {
                ModelState.AddModelError("Input.LicenseKey", "License key is required.");
                return Page();
            }

            var notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();
            var targetTenant = context.Scope == LicenseTenantScope.Platform ? null : context.TenantId;
            var result = await _licenseService.InstallLicenseAsync(
                cleanedKey,
                targetTenant,
                GetUserId(),
                notes,
                cancellationToken).ConfigureAwait(false);

            var now = _timeProvider.GetUtcNow();
            ValidationPreview = LicenseDtoMapper.ToDto(result, now);
            if (ValidationPreview.License is not null)
            {
                ValidationSummary = BuildSummary(ValidationPreview.License, now);
            }

            if (!result.IsValid || result.LicenseInfo is null)
            {
                ValidationError = LicenseDtoMapper.ToErrorDto(result);
                ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
                return Page();
            }

            TempData["SuccessMessage"] = context.Scope == LicenseTenantScope.Platform
                ? "Platform license installed successfully."
                : "Tenant license installed successfully.";

            return RedirectToPage("Index", new
            {
                scope = context.Scope,
                tenantId = context.Scope == LicenseTenantScope.Tenant ? context.TenantId : null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install license for scope {Scope} and tenant {TenantId}", context.Scope, context.TenantId);
            ModelState.AddModelError(string.Empty, "Failed to install the license. Please try again.");
            return Page();
        }
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

    private Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
        return Guid.TryParse(value, out var id) ? id : null;
    }

    public sealed class InstallInput
    {
        [Required]
        [StringLength(2000, ErrorMessage = "License keys are limited to 2000 characters.")]
        [Display(Name = "License key (JWS)")]
        public string LicenseKey { get; set; } = string.Empty;

        [StringLength(1000, ErrorMessage = "Notes are limited to 1000 characters.")]
        [Display(Name = "Notes (optional)")]
        public string? Notes { get; set; }
    }
}
