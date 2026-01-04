using Microsoft.AspNetCore.Http;

namespace MrWhoOidc.WebAuth.Handlers;

public interface ICheckSessionHandler
{
    Task<IResult> HandleAsync(HttpContext ctx);
}

public sealed class CheckSessionHandler : ICheckSessionHandler
{
    public Task<IResult> HandleAsync(HttpContext ctx)
    {
        // OIDC Session Management: check_session_iframe
        // This endpoint must be embeddable by relying parties.
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.Headers["Pragma"] = "no-cache";

        // Minimal check_session_iframe HTML implementation.
        // RP sends: postMessage("<client_id> <session_state>") from its own origin.
        // IFrame responds: "unchanged" | "changed" | "error".
        //
        // session_state algorithm: base64url(sha256(client_id + " " + origin + " " + opbs + " " + salt)) + "." + salt
        // where opbs is read from a non-HttpOnly cookie.
        var html = "<!DOCTYPE html>\n" +
                   "<html><head><meta charset=\"utf-8\" />" +
                   "<title>check_session_iframe</title></head>\n" +
                   "<body>\n" +
                   "<script>\n" +
                   "(function(){\n" +
                   "  function getCookie(name){\n" +
                   "    var parts = (document.cookie || '').split(';');\n" +
                   "    for (var i=0;i<parts.length;i++){\n" +
                   "      var p = parts[i].trim();\n" +
                   "      if (!p) continue;\n" +
                   "      var eq = p.indexOf('=');\n" +
                   "      if (eq < 0) continue;\n" +
                   "      var k = p.substring(0, eq).trim();\n" +
                   "      if (k === name) return decodeURIComponent(p.substring(eq+1));\n" +
                   "    }\n" +
                   "    return '';\n" +
                   "  }\n" +
                   "  function base64UrlFromBytes(bytes){\n" +
                   "    var bin = '';\n" +
                   "    for (var i=0;i<bytes.length;i++){ bin += String.fromCharCode(bytes[i]); }\n" +
                   "    var b64 = btoa(bin).replace(/\\+/g,'-').replace(/\\//g,'_').replace(/=+$/,'');\n" +
                   "    return b64;\n" +
                   "  }\n" +
                   "  async function sha256Base64Url(text){\n" +
                   "    var data = new TextEncoder().encode(text);\n" +
                   "    var digest = await crypto.subtle.digest('SHA-256', data);\n" +
                   "    return base64UrlFromBytes(new Uint8Array(digest));\n" +
                   "  }\n" +
                   "  async function computeSessionState(clientId, origin, opbs, salt){\n" +
                   "    var input = clientId + ' ' + origin + ' ' + opbs + ' ' + salt;\n" +
                   "    var hash = await sha256Base64Url(input);\n" +
                   "    return hash + '.' + salt;\n" +
                   "  }\n" +
                   "  window.addEventListener('message', async function(e){\n" +
                   "    try {\n" +
                   "      if (typeof e.data !== 'string') return;\n" +
                   "      var msg = e.data.trim();\n" +
                   "      if (!msg) return;\n" +
                   "      var parts = msg.split(' ');\n" +
                   "      if (parts.length < 2) { e.source.postMessage('error', e.origin); return; }\n" +
                   "      var clientId = parts[0];\n" +
                   "      var sessionState = parts[1];\n" +
                   "      var dot = sessionState.lastIndexOf('.');\n" +
                   "      if (dot < 0) { e.source.postMessage('error', e.origin); return; }\n" +
                   "      var salt = sessionState.substring(dot + 1);\n" +
                   "      var opbs = getCookie('mrwho.opbs');\n" +
                   "      var expected = await computeSessionState(clientId, e.origin, opbs, salt);\n" +
                   "      e.source.postMessage(expected === sessionState ? 'unchanged' : 'changed', e.origin);\n" +
                   "    } catch (err) {\n" +
                   "      try { e.source.postMessage('error', e.origin); } catch (_) {}\n" +
                   "    }\n" +
                   "  }, false);\n" +
                   "})();\n" +
                   "</script>\n" +
                   "</body></html>\n";

        return Task.FromResult(Results.Text(html, "text/html; charset=utf-8"));
    }
}
