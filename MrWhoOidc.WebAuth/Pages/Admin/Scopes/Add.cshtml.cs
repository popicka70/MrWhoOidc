using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin.Scopes;

/// <summary>
/// Add new OAuth/OIDC scopes (global or tenant-scoped).
/// Platform admins can create global scopes (IsGlobal=true).
/// Tenant admins can create tenant-scoped scopes for their tenant.
/// </summary>
[Authorize(Policy = "tenant-admin")]
public class AddModel(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IAuthorizationService authorizationService,
    IScopeResolver scopeResolver,
    IScopeNameValidator scopeNameValidator,
    IMultiTenancyOptions multiTenancyOptions) : TenantAwarePageModel(tenantAccessor, multiTenancyOptions)
{
    public class AddInput
    {
        [Required, StringLength(100)]
        public string Name { get; set; } = string.Empty;

        [StringLength(200)]
        public string? Description { get; set; }

        public bool IsExposed { get; set; } = true;

        // Only platform admins can set this to true
        public bool IsGlobal { get; set; } = false;
    }

    [BindProperty]
    public AddInput Input { get; set; } = new();

    public bool IsPlatformAdmin { get; private set; }

    public async Task OnGetAsync()
    {
        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid) return Page();

        var platformAdminResult = await authorizationService.AuthorizeAsync(User, "platform-admin");
        IsPlatformAdmin = platformAdminResult.Succeeded;

        Input.Name = Input.Name.Trim();

        // Validate: only platform admins can create global scopes
        if (Input.IsGlobal && !IsPlatformAdmin)
        {
            ModelState.AddModelError("Input.IsGlobal", "Only platform administrators can create global scopes.");
            return Page();
        }

        // Validate: tenant admins must create tenant-scoped scopes
        Guid? targetTenantId = null;
        string? tenantSlug = null;
        if (!Input.IsGlobal)
        {
            targetTenantId = TenantAccessor.CurrentTenant?.TenantId;
            tenantSlug = TenantAccessor.CurrentTenant?.Slug;
            if (!targetTenantId.HasValue)
            {
                ModelState.AddModelError(string.Empty, "No tenant context found. Cannot create tenant-scoped scope.");
                return Page();
            }
        }

        // Validate scope name format using the validator
        var nameValidation = scopeNameValidator.ValidateScopeName(Input.Name, Input.IsGlobal, tenantSlug);
        if (!nameValidation.IsValid)
        {
            ModelState.AddModelError("Input.Name", nameValidation.ErrorMessage!);
            return Page();
        }

        // Check if scope name is available
        var isAvailable = await scopeResolver.IsScopeNameAvailableAsync(Input.Name, targetTenantId);
        if (!isAvailable)
        {
            var scopeType = Input.IsGlobal ? "global" : "tenant-scoped";
            ModelState.AddModelError("Input.Name", $"A {scopeType} scope with this name already exists.");
            return Page();
        }

        db.Scopes.Add(new Scope
        {
            Name = Input.Name,
            Description = Input.Description,
            IsExposed = Input.IsExposed,
            IsGlobal = Input.IsGlobal,
            TenantId = targetTenantId
        });

        await db.SaveChangesAsync();
        TempData["Success"] = $"{(Input.IsGlobal ? "Global" : "Tenant-scoped")} scope '{Input.Name}' created successfully.";
        return TenantAwareRedirect("/Admin/Scopes");
    }
}
