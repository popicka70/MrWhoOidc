using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for enforcing read-only mode during impersonation.
/// </summary>
public static class ReadOnlyModeExtensions
{
    /// <summary>
    /// Checks if the current user is impersonating a tenant.
    /// If so, returns a Forbid result with an error message.
    /// Use this at the start of POST handlers to prevent write operations during impersonation.
    /// </summary>
    /// <param name="pageModel">The page model instance</param>
    /// <param name="impersonationService">The impersonation service</param>
    /// <param name="context">The HTTP context</param>
    /// <returns>A ForbidResult if impersonating, otherwise null</returns>
    public static IActionResult? EnforceReadOnlyMode(
        this PageModel pageModel,
        IImpersonationService impersonationService,
        HttpContext context)
    {
        if (impersonationService.IsImpersonating(context))
        {
            pageModel.TempData["Error"] = "⚠️ Cannot perform this action in read-only impersonation mode. Exit impersonation to make changes.";

            // Return Forbid to prevent the action
            return new ForbidResult();
        }

        return null;
    }

    /// <summary>
    /// Checks if the current request is in impersonation mode.
    /// Use this in page models to conditionally disable UI elements.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <param name="impersonationService">The impersonation service</param>
    /// <returns>True if impersonating, false otherwise</returns>
    public static bool IsInReadOnlyMode(
        this HttpContext context,
        IImpersonationService impersonationService)
    {
        return impersonationService.IsImpersonating(context);
    }
}
