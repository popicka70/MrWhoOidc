using Microsoft.AspNetCore.Http;
using System.Net;
using System.Text.Json;

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
        // session_state algorithm: base64url(sha256(client_id + " " + origin + " " + browser_state + " " + salt)) + "." + salt
        // The comparison is done server-side by the /connect/checksession/state endpoint, which recomputes
        // the hash for the client, the sender origin and the browser session the OP cookie points to, and
        // also checks whether that session is still alive (F1): a revoked, expired or idle session answers
        // "changed", so a relying party signs the user out even though the browser never came back to the OP.
        //
        // The script deliberately uses only ES5 + XMLHttpRequest + postMessage: the OIDF conformance suite
        // checks this iframe with its own scripted browser (HtmlUnit), which does not implement fetch() or
        // the WebCrypto API (crypto.subtle). The XHR is synchronous so the whole check — receive the message,
        // ask the server, post the reply — completes inside a single JavaScript job, without depending on
        // background job scheduling (the scripted browser closes the page soon after it loads).
        var nonce = ctx.Items.TryGetValue("csp-nonce", out var nonceValue)
            ? nonceValue as string
            : null;
        var nonceAttribute = string.IsNullOrWhiteSpace(nonce)
            ? string.Empty
            : $" nonce=\"{WebUtility.HtmlEncode(nonce)}\"";

        // The state endpoint lives next to the iframe, so the tenant prefix of the iframe is kept.
        var stateUrl = JsonSerializer.Serialize(
            $"{ctx.Request.PathBase}{ctx.Request.Path}".TrimEnd('/') + "/state");

        var html = "<!DOCTYPE html>\n" +
                   "<html><head><meta charset=\"utf-8\" />" +
                   "<title>check_session_iframe</title></head>\n" +
                   "<body>\n" +
                   "<script" + nonceAttribute + ">\n" +
                   "(function(){\n" +
                   "  var stateUrl = " + stateUrl + ";\n" +
                   "  function check(e){\n" +
                   "    var replied = false;\n" +
                   "    function reply(value){\n" +
                   "      if (replied) return;\n" +
                   "      replied = true;\n" +
                   "      try { e.source.postMessage(value, e.origin); } catch (_) {}\n" +
                   "    }\n" +
                   "    if (typeof e.data !== 'string') return;\n" +
                   "    var msg = (e.data || '').replace(/^\\s+|\\s+$/g, '');\n" +
                   "    if (!msg) { reply('error'); return; }\n" +
                   "    var xhr = new XMLHttpRequest();\n" +
                   "    var url = stateUrl +\n" +
                   "        '?received_message=' + encodeURIComponent(msg) +\n" +
                   "        '&origin=' + encodeURIComponent(e.origin || '');\n" +
                   "    try {\n" +
                   "      xhr.open('GET', url, false);\n" +
                   "      xhr.send();\n" +
                   "      if (xhr.status < 200 || xhr.status >= 300) { reply('error'); return; }\n" +
                   "      var body = JSON.parse(xhr.responseText);\n" +
                   "      reply(body && typeof body.state === 'string' && body.state ? body.state : 'error');\n" +
                   "    } catch (err) {\n" +
                   "      reply('error');\n" +
                   "    }\n" +
                   "  }\n" +
                   "  window.addEventListener('message', check, false);\n" +
                   "})();\n" +
                   "</script>\n" +
                   "</body></html>\n";

        return Task.FromResult(Results.Text(html, "text/html; charset=utf-8"));
    }
}
