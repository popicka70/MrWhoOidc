using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Licensing.Models;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Admin.Dto;

namespace MrWhoOidc.WebAuth.Pages.Admin.License.Platform;

/// <summary>
/// Platform license installation page - allows platform admins to install/update
/// the platform-wide license that governs deployment mode and enterprise features.
/// </summary>
[Authorize(Policy = "platform-admin")]
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

    [BindProperty]
    public InstallInput Input { get; set; } = new();

    public LicenseValidationResponseDto? ValidationPreview { get; private set; }

    public LicenseValidationErrorDto? ValidationError { get; private set; }

    public LicenseSummaryModel? ValidationSummary { get; private set; }

    public IActionResult OnGet()
    {
        return Page();
    }

    public async Task<IActionResult> OnPostValidateAsync(CancellationToken cancellationToken)
    {
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

                // Additional validation: ensure it's a platform license
                if (!string.Equals(ValidationPreview.License.Scope, "platform", StringComparison.OrdinalIgnoreCase))
                {
                    ValidationError = new LicenseValidationErrorDto(
                        "invalid_scope",
                        "This is a tenant license, not a platform license. Use the tenant license page to install tenant-specific licenses.",
                        Array.Empty<FieldValidationErrorDto>());
                    ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
                }
            }

            if (!result.IsValid)
            {
                ValidationError = LicenseDtoMapper.ToErrorDto(result);
                ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate platform license key");
            ModelState.AddModelError(string.Empty, "Failed to validate the license key. Please try again.");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostInstallAsync(CancellationToken cancellationToken)
    {
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

            // First validate to check scope
            var validationResult = await _licenseService.ValidateLicenseKeyAsync(cleanedKey, cancellationToken).ConfigureAwait(false);
            if (validationResult.LicenseInfo is not null &&
                validationResult.LicenseInfo.Scope != LicenseScope.Platform)
            {
                var now = _timeProvider.GetUtcNow();
                ValidationPreview = LicenseDtoMapper.ToDto(validationResult, now);
                if (ValidationPreview.License is not null)
                {
                    ValidationSummary = BuildSummary(ValidationPreview.License, now);
                }
                ValidationError = new LicenseValidationErrorDto(
                    "invalid_scope",
                    "This is a tenant license, not a platform license. Use the tenant license page to install tenant-specific licenses.",
                    Array.Empty<FieldValidationErrorDto>());
                ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
                return Page();
            }

            var notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim();

            // Install with null tenantId for platform license
            var result = await _licenseService.InstallLicenseAsync(
                cleanedKey,
                null, // null = platform license
                GetUserId(),
                notes,
                cancellationToken).ConfigureAwait(false);

            var installNow = _timeProvider.GetUtcNow();
            ValidationPreview = LicenseDtoMapper.ToDto(result, installNow);
            if (ValidationPreview.License is not null)
            {
                ValidationSummary = BuildSummary(ValidationPreview.License, installNow);
            }

            if (!result.IsValid || result.LicenseInfo is null)
            {
                ValidationError = LicenseDtoMapper.ToErrorDto(result);
                ModelState.AddModelError(string.Empty, ValidationError.ErrorDescription);
                return Page();
            }

            TempData["SuccessMessage"] = "Platform license installed successfully.";
            return RedirectToPage("Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install platform license");
            ModelState.AddModelError(string.Empty, "Failed to install the license. Please try again.");
            return Page();
        }
    }

    public string FormatDate(DateTimeOffset value) => FormatDateTime(value);

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
