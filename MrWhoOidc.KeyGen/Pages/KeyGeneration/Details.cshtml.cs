using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Domain.Services;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Pages.KeyGeneration;

public class DetailsModel : PageModel
{
    private readonly KeyGenDbContext _context;
    private readonly IKeyGenerationService _keyGenerationService;
    private readonly ILogger<DetailsModel> _logger;

    public DetailsModel(
        KeyGenDbContext context,
        IKeyGenerationService keyGenerationService,
        ILogger<DetailsModel> logger)
    {
        _context = context;
        _keyGenerationService = keyGenerationService;
        _logger = logger;
    }

    public KeyPairMetadata? Key { get; set; }
    public List<KeyDownloadRecord> DownloadRecords { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(string kid)
    {
        // Fetch key metadata
        Key = await _context.KeyPairMetadata
            .FirstOrDefaultAsync(k => k.Kid == kid);

        if (Key == null)
        {
            return Page();
        }

        // Fetch download records
        DownloadRecords = await _context.KeyDownloadRecords
            .Where(r => r.KeyPairMetadataId == Key.Id)
            .OrderByDescending(r => r.DownloadedAt)
            .ToListAsync();

        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string kid)
    {
        try
        {
            var success = await _keyGenerationService.RevokeKeyAsync(kid);

            if (success)
            {
                SuccessMessage = $"Key {kid} has been revoked successfully.";
                _logger.LogInformation("Key {Kid} revoked from details page", kid);
            }
            else
            {
                ErrorMessage = $"Key {kid} not found.";
                _logger.LogWarning("Attempted to revoke non-existent key {Kid} from details page", kid);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "An error occurred while revoking the key.";
            _logger.LogError(ex, "Error revoking key {Kid} from details page", kid);
        }

        return RedirectToPage(new { kid });
    }
}
