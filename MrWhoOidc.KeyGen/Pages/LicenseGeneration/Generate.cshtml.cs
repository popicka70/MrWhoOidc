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

public class GenerateModel : PageModel
{
    private readonly ILicenseGenerationService _licenseService;
    private readonly ILogger<GenerateModel> _logger;
    private readonly IReadOnlyList<FeatureDefinition> _featureCatalog = FeatureCatalog.GetAll();
    private readonly IReadOnlyList<FeatureDefinition> _platformOnlyFeatures = FeatureCatalog.GetPlatformOnly();
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tierFeatureMap = LicenseTierCatalog.GetTierFeatureMap();

    public GenerateModel(
        ILicenseGenerationService licenseService,
        ILogger<GenerateModel> logger)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    [Required(ErrorMessage = "License tier is required")]
    public string Tier { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "License scope is required")]
    public string Scope { get; set; } = LicenseScopeOptions.Platform;

    [BindProperty]
    [Required(ErrorMessage = "Organization name is required")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "Organization name must be between 2 and 200 characters")]
    public string Organization { get; set; } = string.Empty;

    [BindProperty]
    [Display(Name = "Issued to")]
    [StringLength(200, ErrorMessage = "Issued To must be 200 characters or fewer")]
    public string? IssuedTo { get; set; }

    [BindProperty]
    [Display(Name = "Tenant Id")]
    public Guid? TenantId { get; set; }

    [BindProperty]
    [Display(Name = "Tenant slug")]
    [StringLength(100, ErrorMessage = "Tenant slug must be 100 characters or fewer")]
    public string? TenantSlug { get; set; }

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
    public List<string> SelectedDefaultTenantFeatures { get; set; } = new();

    [BindProperty]
    [StringLength(2000, ErrorMessage = "Limits cannot exceed 2000 characters")]
    public string? Limits { get; set; }

    [BindProperty]
    [StringLength(100, ErrorMessage = "Created By cannot exceed 100 characters")]
    public string? CreatedBy { get; set; }

    public string? GeneratedTokenId { get; set; }
    public string? JwtToken { get; set; }
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<FeatureDefinition> FeatureOptions => _featureCatalog;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TierFeatureMap => _tierFeatureMap;
    public string TierFeatureMapJson => JsonSerializer.Serialize(_tierFeatureMap);

    public bool IsTenantScope => string.Equals(Scope, LicenseScopeOptions.Tenant, StringComparison.OrdinalIgnoreCase);

    public bool IsPlatformScope => string.Equals(Scope, LicenseScopeOptions.Platform, StringComparison.OrdinalIgnoreCase);

    public void OnGet()
    {
        // Initialize default values
        NotBefore = DateTime.UtcNow.Date;
        ExpiresAt = DateTime.UtcNow.Date.AddYears(1);
        Scope = LicenseScopeOptions.Platform;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Clear previous results
        GeneratedTokenId = null;
        JwtToken = null;
        ErrorMessage = null;

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            Scope = (Scope ?? string.Empty).Trim().ToLowerInvariant();
            if (!LicenseScopeOptions.IsValid(Scope))
            {
                ModelState.AddModelError(nameof(Scope), "Select a valid license scope.");
                return Page();
            }

            NormalizeFeatureSelections();
            ApplyTierFeatureDefaults();

            if (IsPlatformScope && SelectedDefaultTenantFeatures.Count > 0)
            {
                var missing = SelectedDefaultTenantFeatures
                    .Where(f => !SelectedFeatures.Any(sf => sf.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (missing.Count > 0)
                {
                    ModelState.AddModelError(nameof(SelectedDefaultTenantFeatures), "Default tenant features must also be enabled on the platform license.");
                }
            }

            if (IsTenantScope)
            {
                if (!TenantId.HasValue)
                {
                    ModelState.AddModelError(nameof(TenantId), "Tenant ID is required for tenant-scoped licenses.");
                }

                if (SelectedFeatures.Any(f => _platformOnlyFeatures.Any(po => po.Key.Equals(f, StringComparison.OrdinalIgnoreCase))))
                {
                    ModelState.AddModelError(nameof(SelectedFeatures), "Tenant licenses cannot include platform-only features (e.g., Multi-tenancy).");
                }

                if (SelectedDefaultTenantFeatures.Count > 0)
                {
                    ModelState.AddModelError(nameof(SelectedDefaultTenantFeatures), "Default tenant feature overrides only apply to platform licenses.");
                    SelectedDefaultTenantFeatures.Clear();
                }
            }
            else
            {
                TenantId = null;
                TenantSlug = null;
            }

            if (!ModelState.IsValid)
            {
                return Page();
            }

            // Additional validation
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

            // Convert DateTime to DateTimeOffset (UTC)
            var notBeforeOffset = new DateTimeOffset(NotBefore, TimeSpan.Zero);
            var expiresAtOffset = new DateTimeOffset(ExpiresAt, TimeSpan.Zero);
            var featuresPayload = SerializeFeaturesPayload(SelectedFeatures);
            var defaultTenantFeaturesPayload = IsPlatformScope
                ? SerializeFeaturesPayload(SelectedDefaultTenantFeatures)
                : null;
            var issuedTo = string.IsNullOrWhiteSpace(IssuedTo) ? null : IssuedTo.Trim();
            var tenantSlug = string.IsNullOrWhiteSpace(TenantSlug) ? null : TenantSlug.Trim();
            var createdBy = string.IsNullOrWhiteSpace(CreatedBy) ? null : CreatedBy.Trim();

            // Generate license token
            var (tokenId, jwtToken) = await _licenseService.GenerateLicenseTokenAsync(
                Tier,
                Organization,
                notBeforeOffset,
                expiresAtOffset,
                Scope,
                issuedTo,
                TenantId,
                tenantSlug,
                featuresPayload,
                trimmedLimits,
                createdBy,
                defaultTenantFeaturesPayload);

            GeneratedTokenId = tokenId;
            JwtToken = jwtToken;

            _logger.LogInformation(
                "License token generated successfully for organization {Organization} with tier {Tier}",
                Organization, Tier);

            return Page();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = $"Validation error: {ex.Message}";
            _logger.LogWarning(ex, "Validation error during license generation");
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = "An unexpected error occurred while generating the license token. Please check the logs.";
            _logger.LogError(ex, "Error generating license token");
            return Page();
        }
    }

    private void NormalizeFeatureSelections()
    {
        if (SelectedFeatures.Count > 0)
        {
            SelectedFeatures = SelectedFeatures
                .Where(IsKnownFeature)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (SelectedDefaultTenantFeatures.Count > 0)
        {
            SelectedDefaultTenantFeatures = SelectedDefaultTenantFeatures
                .Where(IsKnownFeature)
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

        var filteredFeatures = IsTenantScope
            ? tierFeatures.Where(feature => !platformOnlyKeys.Contains(feature))
            : tierFeatures;

        foreach (var feature in filteredFeatures)
        {
            if (IsKnownFeature(feature))
            {
                featureSet.Add(feature);
            }
        }

        SelectedFeatures = featureSet
            .OrderBy(f => f, comparer)
            .ToList();
    }

    private bool IsKnownFeature(string feature) => _featureCatalog.Any(def => def.Key.Equals(feature, StringComparison.OrdinalIgnoreCase));

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

        // Check if NotBefore is before ExpiresAt
        if (NotBefore >= ExpiresAt)
        {
            errorMessage = "Not Before date must be earlier than Expiration date.";
            return false;
        }

        // Check if ExpiresAt is in the future
        if (ExpiresAt.Date <= DateTime.UtcNow.Date)
        {
            errorMessage = "Expiration date must be in the future.";
            return false;
        }

        // Warn if NotBefore is too far in the past (more than 1 year)
        if (NotBefore.Date < DateTime.UtcNow.Date.AddYears(-1))
        {
            _logger.LogWarning(
                "Not Before date {NotBefore} is more than 1 year in the past",
                NotBefore);
        }

        return true;
    }

    private bool ValidateJsonFormat(string json, out string? errorMessage)
    {
        errorMessage = null;

        try
        {
            // Simple JSON validation - try to parse
            var trimmed = json.Trim();
            if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}"))
            {
                errorMessage = "Limits must be a valid JSON object (e.g., {\"clients\":10,\"users\":100}).";
                return false;
            }

            // Basic validation - you might want to use System.Text.Json for proper parsing
            System.Text.Json.JsonDocument.Parse(json);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            errorMessage = "Limits must be valid JSON format.";
            return false;
        }
    }
}
