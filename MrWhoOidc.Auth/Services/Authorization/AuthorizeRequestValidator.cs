using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services.Authorization;

public sealed class AuthorizeRequestValidator(AuthDbContext db, IClientStore clients) : IAuthorizeRequestValidator
{
    public async Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.response_type, OAuthConstants.ResponseTypes.Code, StringComparison.Ordinal))
            return Error(OAuthConstants.ErrorCodes.UnsupportedResponseType, "Only response_type=code is supported");

        if (string.IsNullOrWhiteSpace(request.client_id))
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "Missing client_id");

        var client = await clients.FindByClientIdAsync(request.client_id, ct).ConfigureAwait(false);
        if (client is null)
            return Error(OAuthConstants.ErrorCodes.UnauthorizedClient, "Unknown client_id");

        if (string.IsNullOrWhiteSpace(request.redirect_uri))
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "Missing redirect_uri");

        if (!UrlComparison.IsValidAbsolute(request.redirect_uri))
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "redirect_uri must be absolute");

        // Normalized value for comparison (query + fragment removed, path normalized)
        var requestedRedirectNormalized = UrlComparison.NormalizeForAllowList(request.redirect_uri);

        // Enforce per-client login redirect allow-list when configured
        if (!string.IsNullOrWhiteSpace(client.AllowedLoginRedirectUrisJson))
        {
            try
            {
                var allowedRaw = JsonSerializer.Deserialize<string[]>(client.AllowedLoginRedirectUrisJson) ?? Array.Empty<string>();
                if (allowedRaw.Length > 0 && !UrlComparison.IsAllowed(request.redirect_uri, allowedRaw))
                {
                    return Error(OAuthConstants.ErrorCodes.InvalidRequest, "redirect_uri is not allowed for this client");
                }
            }
            catch { /* ignore parse errors */ }
        }

        if (client.RequirePkce)
        {
            if (string.IsNullOrWhiteSpace(request.code_challenge) || !string.Equals(request.code_challenge_method, OAuthConstants.CodeChallengeMethods.S256, StringComparison.Ordinal))
                return Error(OAuthConstants.ErrorCodes.InvalidRequest, "PKCE S256 is required for this client");
        }

        if (string.IsNullOrWhiteSpace(request.nonce))
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "Missing nonce");

        var scopes = (request.scope ?? OidcConstants.Scopes.OpenId).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(OidcConstants.Scopes.OpenId))
            return Error(OAuthConstants.ErrorCodes.InvalidScope, "scope must include 'openid'");

        // Enforce requested scopes ? assigned client scopes (if any assigned)
        var allowedScopes = await db.ClientScopes
            .AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Protected scopes must be explicitly assigned to the client, even if the client
        // has no other scope assignments.
        if (scopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal))
        {
            if (!allowedScopes.Contains(OidcConstants.Scopes.Tenants, StringComparer.Ordinal))
            {
                return Error(OAuthConstants.ErrorCodes.InvalidScope, "The 'tenants' scope is not enabled for this client.");
            }
        }
        if (allowedScopes.Count > 0)
        {
            var invalid = scopes.Where(s => !allowedScopes.Contains(s, StringComparer.Ordinal)).ToArray();
            if (invalid.Length > 0)
            {
                return Error(OAuthConstants.ErrorCodes.InvalidScope, $"The following scopes are not allowed for this client: {string.Join(", ", invalid)}");
            }
        }

        // RFC 8707 resource (optional): must be absolute URI when present
        if (!string.IsNullOrEmpty(request.resource) && !UrlComparison.IsValidAbsolute(request.resource))
            return Error(OAuthConstants.ErrorCodes.InvalidTarget, "resource must be an absolute URI");

        // response_mode (optional): support standard modes and JARM modes
        string? responseMode = request.response_mode;
        if (!string.IsNullOrEmpty(responseMode))
        {
            var validModes = new[]
            {
                OidcConstants.ResponseModes.Query,
                OidcConstants.ResponseModes.Fragment,
                OidcConstants.ResponseModes.FormPost,
                OidcConstants.ResponseModes.QueryJwt,
                OidcConstants.ResponseModes.FormPostJwt
            };
            if (!validModes.Contains(responseMode, StringComparer.Ordinal))
            {
                return Error(OAuthConstants.ErrorCodes.UnsupportedResponseMode, 
                    $"Unsupported response_mode '{responseMode}'. Supported modes: query, fragment, form_post, query.jwt, form_post.jwt");
            }
        }

        return new AuthorizeValidationResult(
            IsValid: true,
            ClientId: client.ClientId,
            RedirectUri: request.redirect_uri,
            Scopes: scopes,
            Nonce: request.nonce,
            CodeChallenge: request.code_challenge,
            CodeChallengeMethod: request.code_challenge_method,
            RequireConsent: client.RequireConsent,
            Resource: request.resource,
            ResponseMode: responseMode,
            State: request.state
        );
    }

    private static AuthorizeValidationResult Error(string code, string description) => new(
        IsValid: false,
        Error: code,
        ErrorDescription: description
    );
}
