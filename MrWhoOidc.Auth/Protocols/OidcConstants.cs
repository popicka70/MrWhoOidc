namespace MrWhoOidc.Auth.Protocols;

/// <summary>
/// OpenID Connect protocol constants.
/// </summary>
public static class OidcConstants
{
    /// <summary>
    /// OpenID Connect subject identifier types.
    /// </summary>
    public static class SubjectTypes
    {
        /// <summary>
        /// A consistent sub value is provided to all clients.
        /// </summary>
        public const string Public = "public";

        /// <summary>
        /// A different sub value is provided to each client (for privacy).
        /// </summary>
        public const string Pairwise = "pairwise";
    }

    /// <summary>
    /// Standard OpenID Connect scope values.
    /// </summary>
    public static class Scopes
    {
        public const string OpenId = "openid";
        public const string Profile = "profile";
        public const string Email = "email";
        public const string Address = "address";
        public const string Phone = "phone";
        public const string OfflineAccess = "offline_access";
        public const string Roles = "roles";

        // Custom scopes
        public const string Tenants = "tenants";

        /// <summary>Default scopes for most OIDC flows.</summary>
        public static readonly string[] DefaultScopes = { OpenId, Profile, Email };

        /// <summary>All standard scopes supported by this server.</summary>
        public static readonly string[] AllStandardScopes = { OpenId, Profile, Email, OfflineAccess, Roles };
    }

    /// <summary>
    /// OpenID Connect response mode values.
    /// </summary>
    public static class ResponseModes
    {
        public const string Query = "query";
        public const string Fragment = "fragment";
        public const string FormPost = "form_post";
        public const string QueryJwt = "query.jwt";
        public const string FragmentJwt = "fragment.jwt";
        public const string FormPostJwt = "form_post.jwt";
    }

    /// <summary>
    /// Standard OpenID Connect claim names.
    /// </summary>
    public static class Claims
    {
        public const string Subject = "sub";
        public const string Name = "name";
        public const string GivenName = "given_name";
        public const string FamilyName = "family_name";
        public const string MiddleName = "middle_name";
        public const string Nickname = "nickname";
        public const string PreferredUsername = "preferred_username";
        public const string Profile = "profile";
        public const string Picture = "picture";
        public const string Website = "website";
        public const string Email = "email";
        public const string EmailVerified = "email_verified";
        public const string Gender = "gender";
        public const string Birthdate = "birthdate";
        public const string Zoneinfo = "zoneinfo";
        public const string Locale = "locale";
        public const string PhoneNumber = "phone_number";
        public const string PhoneNumberVerified = "phone_number_verified";
        public const string Address = "address";
        public const string UpdatedAt = "updated_at";

        // ID Token specific claims
        public const string Nonce = "nonce";
        public const string AuthTime = "auth_time";
        public const string Acr = "acr";
        public const string Amr = "amr";
        public const string Azp = "azp";
        public const string Sid = "sid";
        public const string AtHash = "at_hash";
        public const string CHash = "c_hash";
        public const string SHash = "s_hash";

        // Extensions
        public const string Roles = "roles";
        public const string Realm = "realm";
        public const string Idp = "idp";
        public const string TenantId = "tenant_id";
        public const string Cnf = "cnf";
    }

    /// <summary>
    /// ACR (Authentication Context Class Reference) values used by this server.
    /// </summary>
    public static class AcrValues
    {
        public const string Password = "urn:mrwho:acr:password";
        public const string Mfa = "urn:mrwho:acr:mfa";
        public const string Passkey = "urn:mrwho:acr:passkey";
    }

    /// <summary>
    /// Standard OpenID Connect parameter names.
    /// </summary>
    public static class Parameters
    {
        public const string Nonce = "nonce";
        public const string Display = "display";
        public const string Prompt = "prompt";
        public const string MaxAge = "max_age";
        public const string UiLocales = "ui_locales";
        public const string IdTokenHint = "id_token_hint";
        public const string LoginHint = "login_hint";
        public const string AcrValues = "acr_values";
    }
}
