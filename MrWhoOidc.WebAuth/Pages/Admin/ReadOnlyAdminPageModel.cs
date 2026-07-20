using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin;

/// <summary>
/// Base class for admin pages that enforces read-only mode during tenant support access.
/// All POST requests are automatically blocked when support access is active.
/// </summary>
public abstract class ReadOnlyAdminPageModel : PageModel
{
    protected ITenantSupportAccessService? SupportAccessService { get; private set; }

    /// <summary>
    /// Indicates whether the current request is in read-only mode (support access active).
    /// Use this property in GET handlers to conditionally disable UI elements.
    /// </summary>
    public bool IsReadOnlyMode { get; private set; }

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // Get support access service from DI
        SupportAccessService = context.HttpContext.RequestServices
            .GetService(typeof(ITenantSupportAccessService)) as ITenantSupportAccessService;

        if (SupportAccessService != null)
        {
            IsReadOnlyMode = SupportAccessService.IsSupportAccessActive(context.HttpContext);

            // Block all POST requests during support access
            if (IsReadOnlyMode && context.HttpContext.Request.Method == "POST")
            {
                TempData["Error"] = "Cannot perform this action in read-only support access mode. End support access to make changes.";
                context.Result = new ForbidResult();
                return;
            }
        }

        base.OnPageHandlerExecuting(context);
    }
}
