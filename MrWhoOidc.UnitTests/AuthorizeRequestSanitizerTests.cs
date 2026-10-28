using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// The address-bar sanitizer strips the request parameters of a PAR/JAR authorization request so they do not
/// stay in the browser history. It must keep the parameters the server itself needs: RFC 9126 §4 requires the
/// client_id in the authorization request that carries the request_uri, because the request_uri is bound to the
/// client that pushed it. Dropping it made the sanitized URL fail with "client_id does not match the pushed
/// authorization request", which broke the whole PAR flow.
/// </summary>
[TestClass]
public sealed class AuthorizeRequestSanitizerTests
{
    private static HttpContext Request(string query)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = "/connect/authorize";
        http.Request.QueryString = new QueryString(query);
        return http;
    }

    private static string? RedirectLocation(IResult? result)
        => result is RedirectHttpResult redirect ? redirect.Url : null;

    [TestMethod]
    public void SanitizeAddressBar_WithoutRequestUri_DoesNotRedirect()
    {
        var result = new AuthorizeRequestSanitizer().SanitizeAddressBar(
            Request("?client_id=spa&scope=openid%20profile&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb"));

        Assert.IsNull(result);
    }

    [TestMethod]
    public void SanitizeAddressBar_KeepsTheClientIdOfThePushedRequest()
    {
        var result = new AuthorizeRequestSanitizer().SanitizeAddressBar(
            Request("?client_id=spa&request_uri=https%3A%2F%2Fissuer.example.com%2Fpar%2Fabc&scope=openid&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb"));

        var location = RedirectLocation(result);
        Assert.IsNotNull(location);
        StringAssert.Contains(location, "client_id=spa");
        StringAssert.Contains(location, "request_uri=");
    }

    [TestMethod]
    public void SanitizeAddressBar_StripsTheRequestParameters()
    {
        var result = new AuthorizeRequestSanitizer().SanitizeAddressBar(
            Request("?client_id=spa&request_uri=https%3A%2F%2Fissuer.example.com%2Fpar%2Fabc&scope=openid%20profile&redirect_uri=https%3A%2F%2Fapp.example.com%2Fcb&response_type=code&nonce=n-1&state=s-1"));

        var location = RedirectLocation(result);
        Assert.IsNotNull(location);
        Assert.DoesNotContain("scope=", location);
        Assert.DoesNotContain("redirect_uri=", location);
        Assert.DoesNotContain("response_type=", location);
        Assert.DoesNotContain("nonce=", location);
        // The state is preserved (RFC 9101).
        StringAssert.Contains(location, "state=s-1");
    }
}
