using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "admin")]
public class IndexModel(AuthDbContext db) : PageModel
{
    public IReadOnlyList<IdentityProvider> Providers { get; private set; } = Array.Empty<IdentityProvider>();

    public async Task OnGetAsync()
    {
        Providers = await db.IdentityProviders.AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();
    }

    public sealed record ReorderInput(Guid[] ProviderIds);

    public async Task<IActionResult> OnPostReorderAsync([FromBody] ReorderInput input)
    {
        if (input?.ProviderIds is null || input.ProviderIds.Length == 0)
            return BadRequest(new { error = "empty_order" });

        // Load existing providers and validate ids set equality
        var providers = await db.IdentityProviders.ToListAsync();
        var existingIds = providers.Select(p => p.Id).ToHashSet();
        if (!input.ProviderIds.All(id => existingIds.Contains(id)) || existingIds.Count != input.ProviderIds.Length)
            return BadRequest(new { error = "mismatch", message = "Posted provider list does not match existing set." });

        // Assign sequential SortOrder starting at 1 (leave gap of 10 for manual future insertions if desired?)
        var order = 1;
        foreach (var id in input.ProviderIds)
        {
            var p = providers.First(x => x.Id == id);
            if (p.SortOrder != order)
            {
                p.SortOrder = order;
            }
            order += 1;
        }

        await db.SaveChangesAsync();
        return new JsonResult(new { ok = true });
    }
}
