using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Filters;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Pages.Admin;

/// <summary>
/// Base class for admin pages that enforces read-only mode during impersonation.
/// All POST requests are automatically blocked when impersonating.
/// </summary>
public abstract class ReadOnlyAdminPageModel : PageModel
{
    protected IImpersonationService? ImpersonationService { get; private set; }

    /// <summary>
    /// Indicates whether the current request is in read-only mode (impersonating).
    /// Use this property in GET handlers to conditionally disable UI elements.
    /// </summary>
    public bool IsReadOnlyMode { get; private set; }

    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        // Get impersonation service from DI
        ImpersonationService = context.HttpContext.RequestServices
            .GetService(typeof(IImpersonationService)) as IImpersonationService;

        if (ImpersonationService != null)
        {
            IsReadOnlyMode = ImpersonationService.IsImpersonating(context.HttpContext);

            // Block all POST requests during impersonation
            if (IsReadOnlyMode && context.HttpContext.Request.Method == "POST")
            {
                TempData["Error"] = "⚠️ Cannot perform this action in read-only impersonation mode. Exit impersonation to make changes.";
                context.Result = new ForbidResult();
                return;
            }
        }

        base.OnPageHandlerExecuting(context);
    }
}
