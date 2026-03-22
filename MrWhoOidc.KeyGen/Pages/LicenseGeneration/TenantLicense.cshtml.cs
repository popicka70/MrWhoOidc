using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Domain.Services;

namespace MrWhoOidc.KeyGen.Pages.LicenseGeneration;

/// <summary>
/// Page model for generating tenant sublicenses.
/// Tenant sublicenses are always a subset of the parent platform license capabilities.
/// </summary>
public class TenantLicenseModel : PageModel
{
    private readonly ILicenseGenerationService _licenseService;
    private readonly ILogger<TenantLicenseModel> _logger;
    private readonly IReadOnlyList<FeatureDefinition> _featureCatalog = FeatureCatalog.GetAll();
    private readonly IReadOnlyList<FeatureDefinition> _platformOnlyFeatures = FeatureCatalog.GetPlatformOnly();
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tierFeatureMap = LicenseTierCatalog.GetTierFeatureMap();

    public TenantLicenseModel(
        ILicenseGenerationService licenseService,
        ILogger<TenantLicenseModel> logger)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    [Required(ErrorMessage = "License tier is required")]
    public string Tier { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Organization name is required")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "Organization name must be between 2 and 200 characters")]
    public string Organization { get; set; } = string.Empty;

    [BindProperty]
    [Display(Name = "Issued to")]
    [StringLength(200, ErrorMessage = "Issued To must be 200 characters or fewer")]
    public string? IssuedTo { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Tenant ID is required for sublicenses")]
    [Display(Name = "Tenant ID")]
    public Guid? TenantId { get; set; }

    [BindProperty]
    [Display(Name = "Tenant Slug")]
    [StringLength(100, ErrorMessage = "Tenant slug must be 100 characters or fewer")]
    public string? TenantSlug { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Parent Platform License ID is required")]
    [Display(Name = "Parent Platform License ID (JTI)")]
    public string? ParentLicenseId { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Not Before date is required")]
    [DataType(DataType.Date)]
    public DateTime NotBefore { get; set; } = DateTime.UtcNow.Date;

    [BindProperty]
    [Required(ErrorMessage = "Expiration date is required")]
    [DataType(DataType.Date)]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.Date.AddYears(1);

    [BindProperty]
    public List<string> SelectedFeatures { get; set; } = new();

    [BindProperty]
    [StringLength(2000, ErrorMessage = "Limits cannot exceed 2000 characters")]
    public string? Limits { get; set; }

    [BindProperty]
    [StringLength(100, ErrorMessage = "Created By cannot exceed 100 characters")]
    public string? CreatedBy { get; set; }

    [BindProperty]
    [Display(Name = "Allowed Issuers")]
    public string? AllowedIssuers { get; set; }

    public string? GeneratedTokenId { get; set; }
    public string? JwtToken { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Features available for tenant licenses (excludes platform-only features).
    /// </summary>
    public IReadOnlyList<FeatureDefinition> FeatureOptions
        => _featureCatalog.Where(f => !f.IsPlatformOnly).ToList();

    public IReadOnlyDictionary<string, IReadOnlyList<string>> TierFeatureMap => _tierFeatureMap;
    public string TierFeatureMapJson => JsonSerializer.Serialize(_tierFeatureMap);

    public void OnGet()
    {
        NotBefore = DateTime.UtcNow.Date;
        ExpiresAt = DateTime.UtcNow.Date.AddYears(1);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        GeneratedTokenId = null;
        JwtToken = null;
        ErrorMessage = null;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            // Validate required fields
            if (!TenantId.HasValue)
            {
                ModelState.AddModelError(nameof(TenantId), "Tenant ID is required for sublicenses.");
                return Page();
            }

            if (string.IsNullOrWhiteSpace(ParentLicenseId))
            {
                ModelState.AddModelError(nameof(ParentLicenseId),
                    "Parent Platform License ID is required. Copy the JTI from the platform license.");
                return Page();
            }

            // Validate no platform-only features are selected
            var platformOnlyKeys = new HashSet<string>(_platformOnlyFeatures.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);
            var invalidFeatures = SelectedFeatures.Where(f => platformOnlyKeys.Contains(f)).ToList();
            if (invalidFeatures.Count > 0)
            {
                ModelState.AddModelError(nameof(SelectedFeatures),
                    $"Tenant sublicenses cannot include platform-only features: {string.Join(", ", invalidFeatures)}");
                return Page();
            }

            NormalizeFeatureSelections();
            ApplyTierFeatureDefaults();

            if (!ModelState.IsValid)
            {
                return Page();
            }

            // Validate Allowed Issuers for non-community tiers
            if (!string.Equals(Tier, "community", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(AllowedIssuers))
                {
                    ModelState.AddModelError(nameof(AllowedIssuers),
                        "Allowed Issuers are mandatory for non-community licenses.");
                    return Page();
                }
            }

            if (!ValidateDates(out var dateError))
            {
                ErrorMessage = dateError;
                return Page();
            }

            var trimmedLimits = string.IsNullOrWhiteSpace(Limits) ? null : Limits.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedLimits) && !ValidateJsonFormat(trimmedLimits, out var jsonError))
            {
                ErrorMessage = jsonError;
                return Page();
            }

            var notBeforeOffset = new DateTimeOffset(NotBefore, TimeSpan.Zero);
            var expiresAtOffset = new DateTimeOffset(ExpiresAt, TimeSpan.Zero);
            var featuresPayload = SerializeFeaturesPayload(SelectedFeatures);
            var issuedTo = string.IsNullOrWhiteSpace(IssuedTo) ? TenantSlug : IssuedTo.Trim();
            var tenantSlug = string.IsNullOrWhiteSpace(TenantSlug) ? null : TenantSlug.Trim();
            var createdBy = string.IsNullOrWhiteSpace(CreatedBy) ? null : CreatedBy.Trim();
            var parentLicenseId = ParentLicenseId!.Trim();

            string? allowedIssuersPayload = null;
            if (!string.IsNullOrWhiteSpace(AllowedIssuers))
            {
                var issuers = AllowedIssuers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (issuers.Length > 0)
                {
                    allowedIssuersPayload = JsonSerializer.Serialize(issuers);
                }
            }

            var (tokenId, jwtToken) = await _licenseService.GenerateLicenseTokenAsync(
                Tier,
                Organization,
                notBeforeOffset,
                expiresAtOffset,
                LicenseScopeOptions.Tenant,
                issuedTo,
                TenantId,
                tenantSlug,
                featuresPayload,
                trimmedLimits,
                createdBy,
                defaultTenantFeatures: null,
                allowedIssuersPayload,
                deploymentMode: null,  // Not applicable for tenant licenses
                parentLicenseId);

            GeneratedTokenId = tokenId;
            JwtToken = jwtToken;

            _logger.LogInformation(
                "Tenant sublicense generated for tenant {TenantId} ({TenantSlug}) under parent license {ParentLicenseId}",
                TenantId, TenantSlug ?? "no-slug", parentLicenseId);

            return Page();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = $"Validation error: {ex.Message}";
            _logger.LogWarning(ex, "Validation error during tenant sublicense generation");
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = "An unexpected error occurred while generating the sublicense. Please check the logs.";
            _logger.LogError(ex, "Error generating tenant sublicense");
            return Page();
        }
    }

    private void NormalizeFeatureSelections()
    {
        var platformOnlyKeys = new HashSet<string>(_platformOnlyFeatures.Select(f => f.Key), StringComparer.OrdinalIgnoreCase);

        if (SelectedFeatures.Count > 0)
        {
            SelectedFeatures = SelectedFeatures
                .Where(f => IsKnownFeature(f) && !platformOnlyKeys.Contains(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void ApplyTierFeatureDefaults()
    {
        var normalizedTier = LicenseTierCatalog.NormalizeTier(Tier);
        if (normalizedTier is null)
        {
            return;
        }

        var tierFeatures = LicenseTierCatalog.GetFeaturesForTier(normalizedTier);
        if (tierFeatures.Count == 0)
        {
            return;
        }

        var comparer = StringComparer.OrdinalIgnoreCase;
        var featureSet = new HashSet<string>(SelectedFeatures ?? Enumerable.Empty<string>(), comparer);
        var platformOnlyKeys = new HashSet<string>(_platformOnlyFeatures.Select(f => f.Key), comparer);

        // Only add tier features that are not platform-only
        foreach (var feature in tierFeatures)
        {
            if (IsKnownFeature(feature) && !platformOnlyKeys.Contains(feature))
            {
                featureSet.Add(feature);
            }
        }

        SelectedFeatures = featureSet.OrderBy(f => f, comparer).ToList();
    }

    private bool IsKnownFeature(string feature)
        => _featureCatalog.Any(def => def.Key.Equals(feature, StringComparison.OrdinalIgnoreCase));

    private static string? SerializeFeaturesPayload(IEnumerable<string> features)
    {
        var array = features?
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return array is { Length: > 0 }
            ? JsonSerializer.Serialize(array)
            : null;
    }

    private bool ValidateDates(out string? errorMessage)
    {
        errorMessage = null;

        if (NotBefore >= ExpiresAt)
        {
            errorMessage = "Not Before date must be earlier than Expiration date.";
            return false;
        }

        if (ExpiresAt.Date <= DateTime.UtcNow.Date)
        {
            errorMessage = "Expiration date must be in the future.";
            return false;
        }

        if (NotBefore.Date < DateTime.UtcNow.Date.AddYears(-1))
        {
            _logger.LogWarning("Not Before date {NotBefore} is more than 1 year in the past", NotBefore);
        }

        return true;
    }

    private bool ValidateJsonFormat(string json, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                errorMessage = "Limits must be a valid JSON object (e.g., {\"clients\":5,\"users\":50}).";
                return false;
            }

            JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            errorMessage = "Limits must be valid JSON format.";
            return false;
        }
    }
}
