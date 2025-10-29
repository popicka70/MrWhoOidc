using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Pages.LicenseGeneration;

public class ListModel : PageModel
{
    private readonly KeyGenDbContext _dbContext;
    private readonly ILogger<ListModel> _logger;

    public ListModel(
        KeyGenDbContext dbContext,
        ILogger<ListModel> logger)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public List<LicenseTokenMetadata> Licenses { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? TierFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? OrganizationFilter { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? StatusFilter { get; set; }

    public async Task OnGetAsync()
    {
        try
        {
            var query = _dbContext.LicenseTokenMetadata.AsQueryable();

            // Apply tier filter
            if (!string.IsNullOrWhiteSpace(TierFilter))
            {
                query = query.Where(l => l.Tier == TierFilter);
            }

            // Apply organization filter
            if (!string.IsNullOrWhiteSpace(OrganizationFilter))
            {
                query = query.Where(l => EF.Functions.Like(l.Organization, $"%{OrganizationFilter}%"));
            }

            // Apply status filter (expired vs valid)
            if (!string.IsNullOrWhiteSpace(StatusFilter))
            {
                var now = DateTimeOffset.UtcNow;
                
                if (StatusFilter.Equals("valid", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(l => l.ValidUntil > now);
                }
                else if (StatusFilter.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    query = query.Where(l => l.ValidUntil <= now);
                }
            }

            // Order by most recently generated first
            // Load to memory first since SQLite doesn't support DateTimeOffset ordering
            var allLicenses = await query.ToListAsync();
            Licenses = allLicenses.OrderByDescending(l => l.GeneratedAt).ToList();

            _logger.LogInformation(
                "Retrieved {Count} license tokens with filters: Tier={Tier}, Org={Organization}, Status={Status}",
                Licenses.Count, TierFilter ?? "All", OrganizationFilter ?? "All", StatusFilter ?? "All");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving license token list");
            Licenses = new List<LicenseTokenMetadata>();
        }
    }
}
