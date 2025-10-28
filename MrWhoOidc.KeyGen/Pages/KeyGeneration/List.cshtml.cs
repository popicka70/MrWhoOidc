using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Models;
using MrWhoOidc.KeyGen.Persistence;

namespace MrWhoOidc.KeyGen.Pages.KeyGeneration;

public class ListModel : PageModel
{
    private readonly KeyGenDbContext _context;

    public ListModel(KeyGenDbContext context)
    {
        _context = context;
    }

    public List<KeyPairMetadata> Keys { get; set; } = new();

    public async Task OnGetAsync(string? status = null, string? algorithm = null)
    {
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

        // Order by created date descending (newest first)
        Keys = await query
            .OrderByDescending(k => k.CreatedAt)
            .ToListAsync();
    }
}
