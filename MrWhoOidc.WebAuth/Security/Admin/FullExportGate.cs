using Microsoft.AspNetCore.Authorization;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.WebAuth.Security.Admin;

/// <summary>
/// A full configuration export carries upstream IdP client secrets, the private keys MrWhoOidc signs upstream
/// requests with and client secret hashes. It is for platform admins only, and never through a tenant support
/// session (whose read-only mode would otherwise reach it with a GET). Everyone else gets the obfuscated export.
/// </summary>
internal static class FullExportGate
{
    public static bool IsFullMode(string? mode) => string.Equals(mode?.Trim(), "full", StringComparison.OrdinalIgnoreCase);

    public static async Task<bool> IsAllowedAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var supportAccess = services.GetService<ITenantSupportAccessService>();
        var authorization = services.GetService<IAuthorizationService>();
        if (supportAccess is null || authorization is null)
        {
            return false;
        }

        try
        {
            if (await supportAccess.IsSupportAccessActiveAsync(http).ConfigureAwait(false))
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            // No session available: support access cannot be ruled out, so fail closed.
            return false;
        }

        return (await authorization.AuthorizeAsync(http.User, "platform-admin").ConfigureAwait(false)).Succeeded;
    }

    public static IResult Forbidden() => Results.Json(
        new { error = "forbidden", error_description = "A full export is available to platform administrators outside support sessions only. Use mode=obfuscated." },
        statusCode: StatusCodes.Status403Forbidden);
}
