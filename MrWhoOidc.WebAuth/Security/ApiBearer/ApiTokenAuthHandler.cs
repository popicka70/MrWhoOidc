using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Security.ApiBearer;

/// <summary>
/// Options for the API Bearer token authentication scheme.
/// </summary>
public sealed class ApiTokenAuthOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authentication handler that accepts Bearer JWTs issued by this server.
/// Enables CLI tools and API clients to authenticate with access tokens instead
/// of browser cookies, without altering any existing cookie-based auth flow.
/// </summary>
public sealed class ApiTokenAuthHandler(
    IOptionsMonitor<ApiTokenAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITokenValidator tokenValidator,
    IOptions<AuthOptions> authOptions)
    : AuthenticationHandler<ApiTokenAuthOptions>(options, logger, encoder)
{
    internal const string SchemeName = "api-bearer";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = authorization["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
            return AuthenticateResult.Fail("Empty bearer token.");

        // Peek at the issuer by decoding the JWT (without signature validation).
        // This is safe — full validation happens in ITokenValidator below.
        string? issuer;
        try
        {
            issuer = new JwtSecurityToken(token).Issuer;
        }
        catch
        {
            return AuthenticateResult.Fail("Invalid JWT format.");
        }

        if (string.IsNullOrEmpty(issuer))
            return AuthenticateResult.Fail("Token missing issuer claim.");

        var (ok, principal, error) = await tokenValidator.ValidateAsync(token, issuer, Context.RequestAborted, authOptions.Value.ApiAudiences);
        if (!ok || principal is null)
            return AuthenticateResult.Fail(error ?? "Token validation failed.");

        // Map 'sub' → ClaimTypes.NameIdentifier so that existing authorization
        // handlers (written for cookie auth that maps it automatically) can find
        // the user ID without changes.
        if (principal.Identity is ClaimsIdentity identity &&
            !identity.HasClaim(c => c.Type == ClaimTypes.NameIdentifier))
        {
            var sub = identity.FindFirst("sub")?.Value;
            if (!string.IsNullOrEmpty(sub))
                identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 403;
        return Task.CompletedTask;
    }
}
