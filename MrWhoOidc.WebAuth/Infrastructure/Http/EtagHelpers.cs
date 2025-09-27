using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Infrastructure.Http;

public static class EtagHelpers
{
    public static bool SetConditionalEtag(HttpContext ctx, string etag)
    {
        if (string.IsNullOrEmpty(etag)) return false;
        var inm = ctx.Request.Headers.IfNoneMatch.ToString();
        if (!string.IsNullOrEmpty(inm) && string.Equals(inm, etag, StringComparison.Ordinal))
            return true;
        return false;
    }
}
