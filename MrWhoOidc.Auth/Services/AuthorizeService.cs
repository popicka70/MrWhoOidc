using System.Text.RegularExpressions;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.Auth.Services;

public interface IAuthorizeService
{
    Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default);
}

internal sealed class AuthorizeService(AuthDbContext db, IClientStore clients) : IAuthorizeService
{
    public async Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
        if (!string.Equals(request.response_type, "code", StringComparison.Ordinal))
            return Error("unsupported_response_type", "Only response_type=code is supported");

        if (string.IsNullOrWhiteSpace(request.client_id))
            return Error("invalid_request", "Missing client_id");

        var client = await clients.FindByClientIdAsync(request.client_id, ct).ConfigureAwait(false);
        if (client is null)
            return Error("unauthorized_client", "Unknown client_id");

        if (string.IsNullOrWhiteSpace(request.redirect_uri))
            return Error("invalid_request", "Missing redirect_uri");

        if (!IsValidAbsoluteUri(request.redirect_uri))
            return Error("invalid_request", "redirect_uri must be absolute");

        if (client.RequirePkce)
        {
            if (string.IsNullOrWhiteSpace(request.code_challenge) || !string.Equals(request.code_challenge_method, "S256", StringComparison.Ordinal))
                return Error("invalid_request", "PKCE S256 is required for this client");
        }

        if (string.IsNullOrWhiteSpace(request.nonce))
            return Error("invalid_request", "Missing nonce");

        var scopes = (request.scope ?? "openid").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains("openid"))
            return Error("invalid_scope", "scope must include 'openid'");

        // Enforce requested scopes ? assigned client scopes (if any assigned)
        var allowedScopes = await db.ClientScopes
            .AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (allowedScopes.Count > 0)
        {
            var invalid = scopes.Where(s => !allowedScopes.Contains(s, StringComparer.Ordinal)).ToArray();
            if (invalid.Length > 0)
            {
                return Error("invalid_scope", $"The following scopes are not allowed for this client: {string.Join(", ", invalid)}");
            }
        }

        // RFC 8707 resource (optional): must be absolute URI when present
        if (!string.IsNullOrEmpty(request.resource) && !IsValidAbsoluteUri(request.resource))
            return Error("invalid_target", "resource must be an absolute URI");

        // response_mode (optional): support default (null), query.jwt, form_post.jwt
        string? responseMode = request.response_mode;
        if (!string.IsNullOrEmpty(responseMode))
        {
            if (!string.Equals(responseMode, "query.jwt", StringComparison.Ordinal) &&
                !string.Equals(responseMode, "form_post.jwt", StringComparison.Ordinal))
            {
                return Error("unsupported_response_mode", "Only response_mode=query.jwt or form_post.jwt is supported");
            }
        }

        return new AuthorizeValidationResult
        {
            IsValid = true,
            ClientId = client.ClientId,
            RedirectUri = request.redirect_uri,
            Scopes = scopes,
            Nonce = request.nonce,
            CodeChallenge = request.code_challenge,
            CodeChallengeMethod = request.code_challenge_method,
            RequireConsent = client.RequireConsent,
            Resource = request.resource,
            ResponseMode = responseMode
        };
    }

    static bool IsValidAbsoluteUri(string uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out _);

    static AuthorizeValidationResult Error(string code, string description) => new()
    {
        IsValid = false,
        Error = code,
        ErrorDescription = description
    };
}
