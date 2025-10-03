namespace MrWhoOidc.Auth.Protocols;

/// <summary>
/// OAuth 2.0 and related RFC protocol constants.
/// </summary>
public static class OAuthConstants
{
    /// <summary>
    /// Standard OAuth 2.0 parameter names.
    /// </summary>
    public static class Parameters
    {
        public const string GrantType = "grant_type";
        public const string ClientId = "client_id";
        public const string ClientSecret = "client_secret";
        public const string RedirectUri = "redirect_uri";
        public const string Scope = "scope";
        public const string State = "state";
        public const string Code = "code";
        public const string CodeVerifier = "code_verifier";
        public const string CodeChallenge = "code_challenge";
        public const string CodeChallengeMethod = "code_challenge_method";
        public const string ResponseType = "response_type";
        public const string Nonce = "nonce";
        public const string Resource = "resource";
        public const string Audience = "audience";
        public const string RefreshToken = "refresh_token";
        public const string AccessToken = "access_token";
        public const string IdToken = "id_token";
        public const string TokenType = "token_type";
        public const string ExpiresIn = "expires_in";
        public const string ResponseMode = "response_mode";
        public const string ClientAssertionType = "client_assertion_type";
        public const string ClientAssertion = "client_assertion";
        public const string SubjectToken = "subject_token";
        public const string SubjectTokenType = "subject_token_type";
        public const string RequestedTokenType = "requested_token_type";
        public const string IssuedTokenType = "issued_token_type";
        public const string Error = "error";
        public const string ErrorDescription = "error_description";
        public const string ErrorUri = "error_uri";
    }

    /// <summary>
    /// OAuth 2.0 grant type values.
    /// </summary>
    public static class GrantTypes
    {
        public const string AuthorizationCode = "authorization_code";
        public const string RefreshToken = "refresh_token";
        public const string ClientCredentials = "client_credentials";
        public const string TokenExchange = "urn:ietf:params:oauth:grant-type:token-exchange";
    }

    /// <summary>
    /// OAuth 2.0 response type values.
    /// </summary>
    public static class ResponseTypes
    {
        public const string Code = "code";
        public const string Token = "token";
        public const string IdToken = "id_token";
    }

    /// <summary>
    /// OAuth 2.0 error codes as defined in RFC 6749 and related RFCs.
    /// </summary>
    public static class ErrorCodes
    {
        public const string InvalidRequest = "invalid_request";
        public const string InvalidGrant = "invalid_grant";
        public const string InvalidClient = "invalid_client";
        public const string UnauthorizedClient = "unauthorized_client";
        public const string UnsupportedGrantType = "unsupported_grant_type";
        public const string InvalidScope = "invalid_scope";
        public const string InvalidToken = "invalid_token";
        public const string AccessDenied = "access_denied";
        public const string UnsupportedResponseType = "unsupported_response_type";
        public const string ServerError = "server_error";
        public const string TemporarilyUnavailable = "temporarily_unavailable";
        public const string InvalidTarget = "invalid_target";
        public const string UnsupportedResponseMode = "unsupported_response_mode";
        public const string InvalidRequestObject = "invalid_request_object";
        public const string RateLimitExceeded = "rate_limit_exceeded";
        public const string SlowDown = "slow_down";
    }

    /// <summary>
    /// OAuth 2.0 token type values.
    /// </summary>
    public static class TokenTypes
    {
        public const string Bearer = "Bearer";
        public const string DPoP = "DPoP";
        public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";
        public const string RefreshToken = "urn:ietf:params:oauth:token-type:refresh_token";
        public const string Jwt = "urn:ietf:params:oauth:token-type:jwt";
        public const string IdToken = "urn:ietf:params:oauth:token-type:id_token";
    }

    /// <summary>
    /// Client assertion type values (RFC 7521, RFC 7523).
    /// </summary>
    public static class ClientAssertionTypes
    {
        public const string JwtBearer = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
    }

    /// <summary>
    /// PKCE code challenge method values (RFC 7636).
    /// </summary>
    public static class CodeChallengeMethods
    {
        public const string Plain = "plain";
        public const string S256 = "S256";
    }
}
