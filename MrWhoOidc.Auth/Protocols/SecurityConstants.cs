namespace MrWhoOidc.Auth.Protocols;

/// <summary>
/// Security-related constants for cryptography and authentication.
/// </summary>
public static class SecurityConstants
{
    /// <summary>
    /// Password hashing algorithm identifiers.
    /// </summary>
    public static class HashAlgorithms
    {
        public const string Argon2id = "argon2id";
        // Legacy support only
    }

    /// <summary>
    /// JWT signing algorithm identifiers (JWA).
    /// </summary>
    public static class JwtAlgorithms
    {
        // RSA algorithms
        public const string RS256 = "RS256";
        public const string RS384 = "RS384";
        public const string RS512 = "RS512";

        // ECDSA algorithms
        public const string ES256 = "ES256";
        public const string ES384 = "ES384";
        public const string ES512 = "ES512";

        // RSASSA-PSS algorithms
        public const string PS256 = "PS256";
        public const string PS384 = "PS384";
        public const string PS512 = "PS512";

        // HMAC algorithms (typically not used for OIDC signing)
        public const string HS256 = "HS256";
        public const string HS384 = "HS384";
        public const string HS512 = "HS512";
    }

    /// <summary>
    /// Elliptic curve identifiers.
    /// </summary>
    public static class EllipticCurves
    {
        public const string P256 = "P-256";
        public const string P384 = "P-384";
        public const string P521 = "P-521";
    }

    /// <summary>
    /// JWT token type header values.
    /// </summary>
    public static class JwtTokenTypes
    {
        public const string Jwt = "JWT";
        public const string AtJwt = "at+jwt";
        public const string LogoutJwt = "logout+jwt";
    }
}
