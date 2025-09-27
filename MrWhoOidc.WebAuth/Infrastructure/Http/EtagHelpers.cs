using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Infrastructure.Http;

public static class EtagHelpers
{
    /// <summary>
    /// Sets the ETag header and evaluates a conditional If-None-Match request.
    /// Returns true if the response should be 304 Not Modified. ETag header is always set when a non-empty etag is provided.
    /// </summary>
    public static bool SetConditionalEtag(HttpContext ctx, string etag)
    {
        if (string.IsNullOrEmpty(etag)) return false;
        ctx.Response.Headers["ETag"] = etag; // ensure ETag present for both 200 and 304 per RFC 9110
        var inm = ctx.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(inm) && string.Equals(inm, etag, StringComparison.Ordinal))
        {
            return true; // caller should emit 304
        }
        return false;
    }
}
