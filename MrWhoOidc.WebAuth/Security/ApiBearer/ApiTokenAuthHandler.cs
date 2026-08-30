using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
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
    IOptions<AuthOptions> authOptions,
    ITenantResolver tenantResolver,
    ITenantAccessor tenantAccessor)
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

        if (tenantAccessor.CurrentTenant is null)
        {
            var tenantContext = await ResolveTenantContextFromIssuerAsync(issuer).ConfigureAwait(false);
            if (tenantContext is not null)
            {
                tenantAccessor.SetTenant(tenantContext);
            }
        }

        if (tenantAccessor.CurrentTenant is null)
            return AuthenticateResult.Fail("Unable to resolve tenant context from token issuer.");

        // Fail-safe: an empty/whitespace ApiAudiences config now throws during validation rather than silently skipping audience checks.
        var (ok, principal, error) = await tokenValidator.ValidateAsync(token, issuer, Context.RequestAborted, authOptions.Value.ApiAudiences);
        if (!ok || principal is null)
            return AuthenticateResult.Fail(error ?? "Token validation failed.");

        // This scheme does not validate DPoP proofs, so it must not honor a DPoP-bound
        // (sender-constrained) access token as a plain bearer token — that would silently strip the
        // proof-of-possession guarantee if such a token leaked. RFC 9449: a resource that observes a
        // cnf.jkt confirmation MUST require a valid DPoP proof. Plain bearer tokens carry no cnf and
        // are unaffected; a DPoP-bound token must be presented on a DPoP-aware endpoint instead.
        if (principal.HasClaim(c => c.Type == "cnf"))
            return AuthenticateResult.Fail("DPoP-bound access tokens are not accepted as bearer tokens on this endpoint.");

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

    private Task<TenantContext?> ResolveTenantContextFromIssuerAsync(string issuer)
    {
        var issuerPath = "/";

        if (Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri))
        {
            issuerPath = string.IsNullOrWhiteSpace(issuerUri.AbsolutePath)
                ? "/"
                : issuerUri.AbsolutePath;
        }
        else if (issuer.StartsWith("/", StringComparison.Ordinal))
        {
            issuerPath = issuer;
        }

        return tenantResolver.ResolveTenantAsync(issuerPath, Context.RequestAborted);
    }
}
