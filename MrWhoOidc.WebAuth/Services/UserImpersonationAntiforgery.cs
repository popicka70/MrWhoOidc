using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using MrWhoOidc.WebAuth.Middleware;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// Mints antiforgery tokens for forms that post to control endpoints while an impersonation overlay is active.
/// <para>
/// The overlay replaces <see cref="HttpContext.User"/> with the target user, but the layout's logout,
/// tenant-switch and impersonation-stop forms post to endpoints that deliberately run as the durable actor.
/// Antiforgery binds request tokens to the current principal and caches the first token per request, so those
/// forms need a token minted against the actor even when the target principal is installed.
/// </para>
/// </summary>
public static class UserImpersonationAntiforgery
{
    private const string TokenItemKey = "MrWhoOidc.ControlFormAntiforgeryToken";

    /// <summary>
    /// Returns the request token for control forms. On an overlaid page the token is generated against an
    /// isolated context that shares the request and response but presents the actor principal, so the
    /// per-request token cache for the target principal is left intact for ordinary business forms.
    /// </summary>
    public static string GetActorBoundRequestToken(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Items[TokenItemKey] is string cached)
        {
            return cached;
        }

        // Both overlay middlewares capture the durable actor. Control forms rendered under either overlay must
        // mint their token for that actor, not for the overlaid subject.
        var actor = context.Items[UserImpersonationMiddleware.ActorPrincipalItemKey] as ClaimsPrincipal
            ?? context.Items[UserAssistanceMiddleware.ActorPrincipalItemKey] as ClaimsPrincipal;
        string token;
        if (actor is null || ReferenceEquals(actor, context.User))
        {
            token = antiforgery.GetAndStoreTokens(context).RequestToken;
        }
        else
        {
            // DefaultAntiforgery caches its token set in HttpContext.Features. A sidebar form may already
            // have minted a token for the target, so an isolated context is required to bind this token to
            // the actor. The isolated context shares the request/response features (and therefore the real
            // antiforgery cookie and response) but has its own feature collection and authentication state.
            var features = new FeatureCollection();
            CopyFeature<IHttpRequestFeature>(context, features);
            CopyFeature<IHttpResponseFeature>(context, features);
            CopyFeature<IHttpResponseBodyFeature>(context, features);
            var actorContext = new DefaultHttpContext(features) { User = actor };
            token = antiforgery.GetAndStoreTokens(actorContext).RequestToken;
        }

        context.Items[TokenItemKey] = token;
        return token;
    }

    private static void CopyFeature<T>(HttpContext source, IFeatureCollection target) where T : class
    {
        var feature = source.Features.Get<T>();
        if (feature is not null)
        {
            target.Set(feature);
        }
    }
}
