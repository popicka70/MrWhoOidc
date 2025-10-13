using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.WebAuth.Extensions;

namespace MrWhoOidc.WebAuth.Pages.Admin.Providers;

[Authorize(Policy = "tenant-admin")]
public class ClaimMappingsModel(
    AuthDbContext db, 
    IClaimMappingService mapper, 
    ILogger<ClaimMappingsModel> logger,
    ITenantAccessor tenantAccessor,
    IMultiTenancyOptions multiTenancyOptions) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }

    public List<Item> Mappings { get; private set; } = new();

    [BindProperty] public EditorInput? Input { get; set; }

    // Inline test mode input (raw JSON) and output
    [BindProperty] public string? SourceJson { get; set; }
    public Dictionary<string, string>? TestOutput { get; private set; }
    public string? ResultJson { get; private set; }

    /// <summary>
    /// Builds a tenant-aware redirect URL for the current page (with Id query parameter).
    /// Only adds tenant prefix when multi-tenancy is enabled.
    /// </summary>
    private IActionResult RedirectToClaimMappings()
    {
        var url = TenantAwareUrlBuilder.BuildTenantPath(
            "/Admin/Providers/ClaimMappings",
            tenantAccessor,
            multiTenancyOptions,
            ("id", Id.ToString()));
        return Redirect(url);
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var exists = await db.IdentityProviders.AsNoTracking().AnyAsync(p => p.Id == Id);
        if (!exists) return NotFound();
        Mappings = await db.IdentityProviderClaimMappings.AsNoTracking()
            .Where(m => m.IdentityProviderId == Id)
            .OrderBy(m => m.Order)
            .Select(m => new Item(m.Id, m.ExternalClaim, m.LocalClaim, m.Transform, m.Order))
            .ToListAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAddAsync()
    {
        if (Input is null) return RedirectToClaimMappings();
        if (!ModelState.IsValid) return await OnGetAsync();
        var e = new IdentityProviderClaimMapping
        {
            IdentityProviderId = Id,
            ExternalClaim = Input.ExternalClaim.Trim(),
            LocalClaim = Input.LocalClaim.Trim(),
            Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim(),
            Order = Input.Order
        };
        db.IdentityProviderClaimMappings.Add(e);
        await db.SaveChangesAsync();
        return RedirectToClaimMappings();
    }

    public async Task<IActionResult> OnPostUpdateAsync(Guid mappingId)
    {
        if (Input is null) return RedirectToClaimMappings();
        if (!ModelState.IsValid) return await OnGetAsync();
        var e = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == mappingId && m.IdentityProviderId == Id);
        if (e is null) return NotFound();
        e.ExternalClaim = Input.ExternalClaim.Trim();
        e.LocalClaim = Input.LocalClaim.Trim();
        e.Transform = string.IsNullOrWhiteSpace(Input.Transform) ? null : Input.Transform.Trim();
        e.Order = Input.Order;
        await db.SaveChangesAsync();
        return RedirectToClaimMappings();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid mappingId)
    {
        var e = await db.IdentityProviderClaimMappings.FirstOrDefaultAsync(m => m.Id == mappingId && m.IdentityProviderId == Id);
        if (e is null) return NotFound();
        db.IdentityProviderClaimMappings.Remove(e);
        await db.SaveChangesAsync();
        return RedirectToClaimMappings();
    }

    public async Task<IActionResult> OnPostTestAsync()
    {
        // Re-load mappings for display regardless of success
        await OnGetAsync();
        if (string.IsNullOrWhiteSpace(SourceJson))
        {
            TestOutput = new();
            return Page();
        }
        Dictionary<string, string?> sourceClaims;
        try
        {
            using var doc = JsonDocument.Parse(SourceJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                ModelState.AddModelError(nameof(SourceJson), "Root must be a JSON object.");
                return Page();
            }
            sourceClaims = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    sourceClaims[prop.Name] = prop.Value.GetString();
                else if (prop.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
                    sourceClaims[prop.Name] = prop.Value.ToString();
                // Ignore null/arrays/objects for simplicity in test UX.
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Claim mapping test JSON parse failed for provider {ProviderId}", Id);
            ModelState.AddModelError(nameof(SourceJson), "Invalid JSON: " + ex.Message);
            return Page();
        }

        TestOutput = await mapper.ApplyAsync(Id, sourceClaims);
        // Pre-serialize result for copy button (compact JSON)
        ResultJson = System.Text.Json.JsonSerializer.Serialize(TestOutput, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
        return Page();
    }

    public sealed record Item(Guid Id, string External, string Local, string? Transform, int Order);

    public sealed class EditorInput
    {
        [Required] public string ExternalClaim { get; set; } = string.Empty;
        [Required] public string LocalClaim { get; set; } = string.Empty;
        public int Order { get; set; }
        public string? Transform { get; set; }
    }
}
