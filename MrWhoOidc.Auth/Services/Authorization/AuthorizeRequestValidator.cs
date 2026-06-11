using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.Auth.Services.Authorization;

public sealed class AuthorizeRequestValidator(
    AuthDbContext db,
    IClientStore clients,
    ILogger<AuthorizeRequestValidator> logger) : IAuthorizeRequestValidator
{
    public async Task<AuthorizeValidationResult> ValidateAsync(AuthorizeRequest request, CancellationToken ct = default)
    {
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

        // Enforce the per-client login redirect allow-list — fail closed. A client with no
        // configured redirect URIs must NOT be allowed to use an arbitrary redirect_uri: that would
        // turn the IdP into an open redirector and let an attacker have authorization codes (and
        // error responses carrying state) delivered to a URL they control.
        string[] allowedRedirectUris;
        try
        {
            allowedRedirectUris = string.IsNullOrWhiteSpace(client.AllowedLoginRedirectUrisJson)
                ? Array.Empty<string>()
                : JsonSerializer.Deserialize<string[]>(client.AllowedLoginRedirectUrisJson) ?? Array.Empty<string>();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "Failed to parse AllowedLoginRedirectUrisJson for client {ClientId}",
                client.ClientId);
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "redirect_uri allow-list is invalid for this client");
        }

        if (allowedRedirectUris.Length == 0 || !UrlComparison.IsAllowed(request.redirect_uri, allowedRedirectUris))
        {
            return Error(OAuthConstants.ErrorCodes.InvalidRequest, "redirect_uri is not allowed for this client");
        }

        AuthorizeValidationResult ClientError(string code, string description) => new(
            IsValid: false,
            Error: code,
            ErrorDescription: description,
            ClientId: client.ClientId,
            RedirectUri: request.redirect_uri,
            ResponseMode: request.response_mode,
            State: request.state
        );

        if (string.IsNullOrWhiteSpace(request.response_type))
            return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, "Missing response_type");

        if (!string.Equals(request.response_type, OAuthConstants.ResponseTypes.Code, StringComparison.Ordinal))
            return ClientError(OAuthConstants.ErrorCodes.UnsupportedResponseType, "Only response_type=code is supported");

        // Require PKCE (S256) when the client opts in, OR whenever the client is public (no client
        // secret). A public client cannot authenticate at the token endpoint, so without PKCE an
        // intercepted authorization code can be redeemed by an attacker (auth-code interception).
        var isPublicClient = string.IsNullOrEmpty(client.ClientSecretHash);
        if (client.RequirePkce || isPublicClient)
        {
            if (string.IsNullOrWhiteSpace(request.code_challenge) || !string.Equals(request.code_challenge_method, OAuthConstants.CodeChallengeMethods.S256, StringComparison.Ordinal))
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, "PKCE S256 is required for this client");
        }

        var scopes = (request.scope ?? OidcConstants.Scopes.OpenId).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!scopes.Contains(OidcConstants.Scopes.OpenId))
            return ClientError(OAuthConstants.ErrorCodes.InvalidScope, "scope must include 'openid'");

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
                return ClientError(OAuthConstants.ErrorCodes.InvalidScope, "The 'tenants' scope is not enabled for this client.");
            }
        }
        if (allowedScopes.Count > 0)
        {
            var invalid = scopes.Where(s => !allowedScopes.Contains(s, StringComparer.Ordinal)).ToArray();
            if (invalid.Length > 0)
            {
                return ClientError(OAuthConstants.ErrorCodes.InvalidScope, $"The following scopes are not allowed for this client: {string.Join(", ", invalid)}");
            }
        }

        // RFC 8707 resource (optional): must be absolute URI when present
        if (!string.IsNullOrEmpty(request.resource) && !UrlComparison.IsValidAbsolute(request.resource))
            return ClientError(OAuthConstants.ErrorCodes.InvalidTarget, "resource must be an absolute URI");

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
                    OidcConstants.ResponseModes.FragmentJwt,
                    OidcConstants.ResponseModes.FormPostJwt
            };
            if (!validModes.Contains(responseMode, StringComparer.Ordinal))
            {
                return ClientError(OAuthConstants.ErrorCodes.UnsupportedResponseMode,
                    $"Unsupported response_mode '{responseMode}'. Supported modes: query, fragment, form_post, query.jwt, fragment.jwt, form_post.jwt");
            }
        }

        // prompt (optional)
        string[]? promptValues = null;
        if (!string.IsNullOrWhiteSpace(request.prompt))
        {
            promptValues = request.prompt
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Trim().ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var supported = new[] { "none", "login", "consent", "select_account" };
            var invalid = promptValues.Where(p => !supported.Contains(p, StringComparer.Ordinal)).ToArray();
            if (invalid.Length > 0)
            {
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest,
                    $"Unsupported prompt value(s): {string.Join(", ", invalid)}");
            }

            if (promptValues.Contains("none", StringComparer.Ordinal) && promptValues.Length > 1)
            {
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, "prompt=none must not be combined with other prompt values");
            }
        }

        // max_age (optional)
        int? maxAgeSeconds = null;
        if (!string.IsNullOrWhiteSpace(request.max_age))
        {
            if (!int.TryParse(request.max_age, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            {
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, "max_age must be a non-negative integer");
            }
            maxAgeSeconds = parsed;
        }

        // acr_values (optional)
        string[]? acrValues = null;
        if (!string.IsNullOrWhiteSpace(request.acr_values))
        {
            acrValues = request.acr_values
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        // OIDC claims parameter (optional): validate and normalize so it can be persisted with the auth code.
        string? normalizedClaimsJson = null;
        if (!string.IsNullOrWhiteSpace(request.claims))
        {
            if (!OidcClaimsRequestParser.TryNormalizeClaimsParameter(
                    request.claims,
                    OidcClaimsRequestParser.DefaultMaxBytes,
                    out var normalized,
                    out var claimsError))
            {
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, claimsError ?? "Invalid claims parameter");
            }
            normalizedClaimsJson = normalized;
        }

        // RFC 9396: authorization_details (optional) — must be a JSON array where each element has a "type" field.
        string? normalizedAuthorizationDetailsJson = null;
        if (!string.IsNullOrWhiteSpace(request.authorization_details))
        {
            var authDetailsError = ValidateAuthorizationDetails(request.authorization_details, out normalizedAuthorizationDetailsJson);
            if (authDetailsError is not null)
                return ClientError(OAuthConstants.ErrorCodes.InvalidRequest, authDetailsError);
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
            State: request.state,
            ClaimsJson: normalizedClaimsJson,
            PromptValues: promptValues,
            MaxAgeSeconds: maxAgeSeconds ?? client.DefaultMaxAge,
            AcrValues: acrValues ?? DeserializeDefaultAcrValues(client.DefaultAcrValuesJson),
            AuthorizationDetailsJson: normalizedAuthorizationDetailsJson
        );
    }

    private static string[]? DeserializeDefaultAcrValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var values = JsonSerializer.Deserialize<string[]>(json);
            return values is { Length: > 0 } ? values : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates RFC 9396 authorization_details value: must be a JSON array where each element is an object with a "type" string field.
    /// Returns null on success, or an error description string on failure. Sets normalizedJson to the compact-serialized form.
    /// </summary>
    private static string? ValidateAuthorizationDetails(string raw, out string? normalizedJson)
    {
        normalizedJson = null;
        if (raw.Length > 65536)
        {
            return "authorization_details value exceeds maximum allowed size";
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return "authorization_details must be a JSON array";

            int index = 0;
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    return $"authorization_details[{index}] must be a JSON object";

                if (!element.TryGetProperty("type", out var typeProp) || typeProp.ValueKind != JsonValueKind.String)
                    return $"authorization_details[{index}] must contain a 'type' string field";

                var typeValue = typeProp.GetString();
                if (string.IsNullOrWhiteSpace(typeValue))
                    return $"authorization_details[{index}].type must not be empty";

                index++;
            }

            if (index == 0)
                return "authorization_details must not be an empty array";

            normalizedJson = doc.RootElement.GetRawText();
            return null;
        }
        catch (JsonException)
        {
            return "authorization_details must be valid JSON";
        }
    }

    private static AuthorizeValidationResult Error(string code, string description) => new(
        IsValid: false,
        Error: code,
        ErrorDescription: description
    );
}
