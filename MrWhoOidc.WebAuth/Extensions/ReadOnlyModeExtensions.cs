using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Extensions;

/// <summary>
/// Extension methods for enforcing read-only mode during tenant support access.
/// </summary>
public static class ReadOnlyModeExtensions
{
    /// <summary>
    /// Checks if the current user has active support access.
    /// If so, returns a Forbid result with an error message.
    /// Use this at the start of POST handlers to prevent write operations during support access.
    /// </summary>
    /// <param name="pageModel">The page model instance</param>
    /// <param name="supportAccessService">The tenant support access service</param>
    /// <param name="context">The HTTP context</param>
    /// <returns>A ForbidResult if support access is active, otherwise null</returns>
    public static IActionResult? EnforceReadOnlyMode(
        this PageModel pageModel,
        ITenantSupportAccessService supportAccessService,
        HttpContext context)
    {
        if (supportAccessService.IsSupportAccessActive(context))
        {
            pageModel.TempData["Error"] = "Cannot perform this action in read-only support access mode. End support access to make changes.";

            // Return Forbid to prevent the action
            return new ForbidResult();
        }

        return null;
    }

    /// <summary>
    /// Checks if the current request has active support access.
    /// Use this in page models to conditionally disable UI elements.
    /// </summary>
    /// <param name="context">The HTTP context</param>
    /// <param name="supportAccessService">The tenant support access service</param>
    /// <returns>True if support access is active, false otherwise</returns>
    public static bool IsInReadOnlyMode(
        this HttpContext context,
        ITenantSupportAccessService supportAccessService)
    {
        return supportAccessService.IsSupportAccessActive(context);
    }
}
