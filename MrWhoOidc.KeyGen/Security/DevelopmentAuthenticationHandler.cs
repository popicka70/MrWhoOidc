using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using MrWhoOidc.KeyGen.Configuration;

namespace MrWhoOidc.KeyGen.Security;

/// <summary>
/// Development-only scheme used when <c>KeyGen:Auth:DisableInDevelopment=true</c>.
/// Signs every request in as a fixed local developer that holds the required role, so the
/// fallback policy and the audit columns behave the same as with a real IdP.
/// </summary>
public sealed class DevelopmentAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "KeyGenDevelopment";
    public const string Subject = "dev-local";
    public const string DisplayName = "Local developer (KeyGen auth disabled)";

    private readonly KeyGenAuthOptions _authOptions;

    public DevelopmentAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<KeyGenAuthOptions> authOptions)
        : base(options, logger, encoder)
    {
        _authOptions = authOptions.Value;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [
                new Claim("sub", Subject),
                new Claim("name", DisplayName),
                new Claim(_authOptions.RoleClaimType, _authOptions.RequiredRole)
            ],
            SchemeName,
            "name",
            _authOptions.RoleClaimType);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
