using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.WebAuth.Observability;
using System.Web;

namespace MrWhoOidc.WebAuth.Handlers.Logout;

/// <summary>
/// Resolves opaque logout redirect references and builds final redirect URIs.
/// </summary>
public sealed class LogoutRedirectResolver(
    AuthDbContext db,
    IAuditSink audit)
{
    /// <summary>
    /// Resolves an opaque reference ID to a validated redirect URI with optional state parameter.
    /// </summary>
    public async Task<IResult> ResolveAndRedirectAsync(string refId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refId))
        {
            return Results.BadRequest();
        }

        var record = await db.LogoutRedirectReferences
            .FirstOrDefaultAsync(r => r.Id == refId, cancellationToken)
            .ConfigureAwait(false);

        if (record is null)
        {
            return Results.BadRequest();
        }

        if (record.ExpiresAt < DateTimeOffset.UtcNow || record.Used)
        {
            return Results.BadRequest();
        }

        record.Used = true;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var dest = record.RedirectUri;

        // Append state if present and not already in the URI
        if (!string.IsNullOrEmpty(record.State))
        {
            var ub = new UriBuilder(dest);
            var q = HttpUtility.ParseQueryString(ub.Query);

            if (string.IsNullOrEmpty(q["state"]))
            {
                q["state"] = record.State;
            }

            ub.Query = q.ToString();
            dest = ub.ToString();
        }

        audit.Emit("logout.redirect.ref.used", new { client_id = record.ClientId });
        return Results.Redirect(dest);
    }
}
