using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.KeyGen.Domain.Services;

namespace MrWhoOidc.KeyGen.Pages.KeyGeneration;

public class GenerateModel : PageModel
{
    private readonly IKeyGenerationService _keyGenerationService;

    public GenerateModel(IKeyGenerationService keyGenerationService)
    {
        _keyGenerationService = keyGenerationService;
    }

    [BindProperty]
    [Required(ErrorMessage = "Algorithm is required")]
    public string Algorithm { get; set; } = string.Empty;

    [BindProperty]
    public int? KeySize { get; set; }

    [BindProperty]
    public string? Curve { get; set; }

    public bool KeyGenerated { get; set; }
    public string? GeneratedKid { get; set; }
    public string? PrivateKeyJwk { get; set; }
    public string? PublicKeyJwks { get; set; }
    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        // Display form
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // Validate algorithm
        var validAlgorithms = new[] { "RS256", "RS384", "RS512", "ES256", "ES384", "ES512", "PS256" };
        if (!validAlgorithms.Contains(Algorithm))
        {
            ModelState.AddModelError(nameof(Algorithm), "Invalid algorithm selected");
        }

        // Determine key type from algorithm
        string keyType;
        if (Algorithm.StartsWith("RS") || Algorithm.StartsWith("PS"))
        {
            keyType = "RSA";

            // Validate RSA parameters
            if (!KeySize.HasValue)
            {
                ModelState.AddModelError(nameof(KeySize), "Key size is required for RSA algorithms");
            }
            else if (KeySize.Value != 2048 && KeySize.Value != 3072 && KeySize.Value != 4096)
            {
                ModelState.AddModelError(nameof(KeySize), "Key size must be 2048, 3072, or 4096");
            }

            // Clear EC parameters
            Curve = null;
        }
        else if (Algorithm.StartsWith("ES"))
        {
            keyType = "EC";

            // Validate EC parameters
            if (string.IsNullOrEmpty(Curve))
            {
                ModelState.AddModelError(nameof(Curve), "Curve is required for ECDSA algorithms");
            }
            else if (Curve != "P-256" && Curve != "P-384" && Curve != "P-521")
            {
                ModelState.AddModelError(nameof(Curve), "Curve must be P-256, P-384, or P-521");
            }

            // Clear RSA parameters
            KeySize = null;
        }
        else
        {
            ModelState.AddModelError(nameof(Algorithm), "Unsupported algorithm");
            return Page();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            // Generate key pair
            var (kid, privateKeyJwk, publicKeyJwks) = await _keyGenerationService.GenerateKeyPairAsync(
                Algorithm,
                keyType,
                KeySize,
                Curve,
                createdBy: null // TODO: Add authentication to track who generated the key
            );

            // Set success state
            KeyGenerated = true;
            GeneratedKid = kid;
            PrivateKeyJwk = privateKeyJwk;
            PublicKeyJwks = publicKeyJwks;

            return Page();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to generate key pair: {ex.Message}";
            return Page();
        }
    }
}
