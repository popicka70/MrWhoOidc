using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.KeyGen.Domain.Services;

namespace MrWhoOidc.KeyGen.Pages.LicenseGeneration;

public class GenerateModel : PageModel
{
    private readonly ILicenseGenerationService _licenseService;
    private readonly ILogger<GenerateModel> _logger;

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
    [Required(ErrorMessage = "Organization name is required")]
    [StringLength(200, MinimumLength = 2, ErrorMessage = "Organization name must be between 2 and 200 characters")]
    public string Organization { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Not Before date is required")]
    [DataType(DataType.Date)]
    public DateTime NotBefore { get; set; } = DateTime.UtcNow.Date;

    [BindProperty]
    [Required(ErrorMessage = "Expiration date is required")]
    [DataType(DataType.Date)]
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.Date.AddYears(1);

    [BindProperty]
    [StringLength(500, ErrorMessage = "Features cannot exceed 500 characters")]
    public string? Features { get; set; }

    [BindProperty]
    [StringLength(2000, ErrorMessage = "Limits cannot exceed 2000 characters")]
    public string? Limits { get; set; }

    [BindProperty]
    [StringLength(100, ErrorMessage = "Created By cannot exceed 100 characters")]
    public string? CreatedBy { get; set; }

    public string? GeneratedTokenId { get; set; }
    public string? JwtToken { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        // Initialize default values
        NotBefore = DateTime.UtcNow.Date;
        ExpiresAt = DateTime.UtcNow.Date.AddYears(1);
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
            // Additional validation
            if (!ValidateDates(out var dateError))
            {
                ErrorMessage = dateError;
                return Page();
            }

            if (!string.IsNullOrWhiteSpace(Limits) && !ValidateJsonFormat(Limits, out var jsonError))
            {
                ErrorMessage = jsonError;
                return Page();
            }

            // Convert DateTime to DateTimeOffset (UTC)
            var notBeforeOffset = new DateTimeOffset(NotBefore, TimeSpan.Zero);
            var expiresAtOffset = new DateTimeOffset(ExpiresAt, TimeSpan.Zero);

            // Generate license token
            var (tokenId, jwtToken) = await _licenseService.GenerateLicenseTokenAsync(
                Tier,
                Organization,
                notBeforeOffset,
                expiresAtOffset,
                Features,
                Limits,
                CreatedBy);

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
