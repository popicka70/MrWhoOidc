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
/// Page model for generating platform licenses with deployment mode support.
/// Platform licenses apply to the entire OIDC platform and can be configured for
/// single-tenant or multi-tenant deployments.
/// </summary>
public class PlatformLicenseModel : PageModel
{
    private readonly ILicenseGenerationService _licenseService;
    private readonly ILogger<PlatformLicenseModel> _logger;
    private readonly IReadOnlyList<FeatureDefinition> _featureCatalog = FeatureCatalog.GetAll();
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _tierFeatureMap = LicenseTierCatalog.GetTierFeatureMap();

    public PlatformLicenseModel(
        ILicenseGenerationService licenseService,
        ILogger<PlatformLicenseModel> logger)
    {
        _licenseService = licenseService ?? throw new ArgumentNullException(nameof(licenseService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    [BindProperty]
    [Required(ErrorMessage = "License tier is required")]
    public string Tier { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Deployment mode is required")]
    public string DeploymentMode { get; set; } = "multi-tenant";

    [BindProperty]
    [Required(ErrorMessage = "Organization name is required")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "Organization name must be between 2 and 200 characters")]
    public string Organization { get; set; } = string.Empty;

    [BindProperty]
    [Display(Name = "Issued to")]
    [StringLength(200, ErrorMessage = "Issued To must be 200 characters or fewer")]
    public string? IssuedTo { get; set; }

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

    [BindProperty]
    [Display(Name = "Allowed Issuers")]
    public string? AllowedIssuers { get; set; }

    public string? GeneratedTokenId { get; set; }
    public string? JwtToken { get; set; }
    public string? ErrorMessage { get; set; }

    public IReadOnlyList<FeatureDefinition> FeatureOptions => _featureCatalog;
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TierFeatureMap => _tierFeatureMap;
    public string TierFeatureMapJson => JsonSerializer.Serialize(_tierFeatureMap);

    public bool IsMultiTenant => string.Equals(DeploymentMode, "multi-tenant", StringComparison.OrdinalIgnoreCase);

    public void OnGet()
    {
        NotBefore = DateTime.UtcNow.Date;
        ExpiresAt = DateTime.UtcNow.Date.AddYears(1);
        DeploymentMode = "multi-tenant";
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
            // Validate deployment mode
            DeploymentMode = (DeploymentMode ?? string.Empty).Trim().ToLowerInvariant();
            if (DeploymentMode != "single-tenant" && DeploymentMode != "multi-tenant")
            {
                ModelState.AddModelError(nameof(DeploymentMode), "Select a valid deployment mode.");
                return Page();
            }

            NormalizeFeatureSelections();
            ApplyTierFeatureDefaults();

            // Single-tenant mode: no default tenant features needed
            if (DeploymentMode == "single-tenant")
            {
                SelectedDefaultTenantFeatures.Clear();
            }
            else if (SelectedDefaultTenantFeatures.Count > 0)
            {
                // Validate default tenant features are subset of platform features
                var missing = SelectedDefaultTenantFeatures
                    .Where(f => !SelectedFeatures.Any(sf => sf.Equals(f, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (missing.Count > 0)
                {
                    ModelState.AddModelError(nameof(SelectedDefaultTenantFeatures), 
                        "Default tenant features must also be enabled on the platform license.");
                }
            }

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
            var defaultTenantFeaturesPayload = IsMultiTenant
                ? SerializeFeaturesPayload(SelectedDefaultTenantFeatures)
                : null;
            var issuedTo = string.IsNullOrWhiteSpace(IssuedTo) ? null : IssuedTo.Trim();
            var createdBy = string.IsNullOrWhiteSpace(CreatedBy) ? null : CreatedBy.Trim();

            string? allowedIssuersPayload = null;
            if (!string.IsNullOrWhiteSpace(AllowedIssuers))
            {
                var issuers = AllowedIssuers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (issuers.Length > 0)
                {
                    allowedIssuersPayload = JsonSerializer.Serialize(issuers);
                }
            }

            // Convert deployment mode to claim value
            var deploymentModeValue = DeploymentModeOptions.ToClaimValue(DeploymentMode);

            var (tokenId, jwtToken) = await _licenseService.GenerateLicenseTokenAsync(
                Tier,
                Organization,
                notBeforeOffset,
                expiresAtOffset,
                LicenseScopeOptions.Platform,
                issuedTo,
                tenantId: null,
                tenantSlug: null,
                featuresPayload,
                trimmedLimits,
                createdBy,
                defaultTenantFeaturesPayload,
                allowedIssuersPayload,
                deploymentModeValue,
                parentLicenseId: null);

            GeneratedTokenId = tokenId;
            JwtToken = jwtToken;

            _logger.LogInformation(
                "Platform license generated for organization {Organization} with tier {Tier} and deployment mode {DeploymentMode}",
                Organization, Tier, DeploymentMode);

            return Page();
        }
        catch (ArgumentException ex)
        {
            ErrorMessage = $"Validation error: {ex.Message}";
            _logger.LogWarning(ex, "Validation error during platform license generation");
            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = "An unexpected error occurred while generating the license token. Please check the logs.";
            _logger.LogError(ex, "Error generating platform license token");
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

        foreach (var feature in tierFeatures)
        {
            if (IsKnownFeature(feature))
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
                errorMessage = "Limits must be a valid JSON object (e.g., {\"clients\":10,\"users\":100}).";
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
