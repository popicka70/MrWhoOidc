using Microsoft.AspNetCore.Http;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Security;
using MrWhoOidc.WebAuth.Handlers; // for OidcOptions
using System.Threading.Tasks;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Abstraction for handling a specific OAuth2/OIDC token endpoint grant type.
/// Transitional interface: strategies are invoked after client auth + (early) DPoP validation.
/// </summary>
public interface ITokenGrantHandler
{
    /// <summary>The grant_type string this handler supports (exact match, ordinal).</summary>
    string GrantType { get; }

    /// <summary>
    /// Attempt to handle the request. If the grant_type matches, implement logic and return a result.
    /// If it does not match, return Handled = false.
    /// The handler MUST NOT record global token metrics; TokenHandler orchestrator does that uniformly.
    /// </summary>
    Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context);
}

public sealed record GrantExecutionResult(bool Handled, bool Success, IResult? Result);

/// <summary>
/// Context passed to grant handlers with pre-parsed values and commonly-needed services.
/// </summary>
public sealed class TokenRequestContext
{
    public TokenRequestContext(HttpContext http, string grantType, string clientId, IFormCollection form, OidcOptions options, ITokenService tokens, string? dpopJkt, MrWhoOidc.Auth.Persistence.Client? clientEntity, bool usedPrivateKeyJwt)
    {
        Http = http;
        GrantType = grantType;
        ClientId = clientId;
        Form = form;
        Options = options;
        Tokens = tokens;
        DPoPJkt = dpopJkt;
        ClientEntity = clientEntity;
        UsedPrivateKeyJwt = usedPrivateKeyJwt;
    }
    public HttpContext Http { get; }
    public string GrantType { get; }
    public string ClientId { get; }
    public IFormCollection Form { get; }
    public OidcOptions Options { get; }
    public ITokenService Tokens { get; }
    public string? DPoPJkt { get; }
    public MrWhoOidc.Auth.Persistence.Client? ClientEntity { get; }
    public bool UsedPrivateKeyJwt { get; }
}
