using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Domain.Services;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Pages.KeyGeneration;

public class ListModel : PageModel
{
    private readonly KeyGenDbContext _context;
    private readonly IKeyGenerationService _keyGenerationService;
    private readonly ILogger<ListModel> _logger;

    public ListModel(
        KeyGenDbContext context,
        IKeyGenerationService keyGenerationService,
        ILogger<ListModel> logger)
    {
        _context = context;
        _keyGenerationService = keyGenerationService;
        _logger = logger;
    }

    public List<KeyPairMetadata> Keys { get; set; } = new();

    [TempData]
    public string? SuccessMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public int CurrentPage { get; set; } = 1;
    public int PageSize { get; set; } = 20;
    public int TotalPages { get; set; }
    public int TotalCount { get; set; }

    public async Task OnGetAsync(string? status = null, string? algorithm = null, int page = 1, int pageSize = 20)
    {
        CurrentPage = page;
        PageSize = pageSize;

        var query = _context.KeyPairMetadata.AsQueryable();

        // Filter by status if provided
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(k => k.Status == status);
        }

        // Filter by algorithm if provided
        if (!string.IsNullOrEmpty(algorithm))
        {
            query = query.Where(k => k.Algorithm == algorithm);
        }

        // Get total count for pagination
        TotalCount = await query.CountAsync();
        TotalPages = (int)Math.Ceiling(TotalCount / (double)PageSize);

        // Ensure page is within valid range
        if (CurrentPage < 1) CurrentPage = 1;
        if (CurrentPage > TotalPages && TotalPages > 0) CurrentPage = TotalPages;

        // Order by created date descending (newest first) and apply pagination
        // AsEnumerable() to switch to client-side evaluation for DateTimeOffset ordering
        var allKeys = await query.ToListAsync();
        Keys = allKeys
            .OrderByDescending(k => k.CreatedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToList();
    }

    public async Task<IActionResult> OnPostRevokeAsync(string kid)
    {
        try
        {
            var success = await _keyGenerationService.RevokeKeyAsync(kid);

            if (success)
            {
                SuccessMessage = $"Key {kid} has been revoked successfully.";
                _logger.LogInformation("Key {Kid} revoked", kid);
            }
            else
            {
                ErrorMessage = $"Key {kid} not found.";
                _logger.LogWarning("Attempted to revoke non-existent key {Kid}", kid);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = "An error occurred while revoking the key.";
            _logger.LogError(ex, "Error revoking key {Kid}", kid);
        }

        return RedirectToPage();
    }
}
