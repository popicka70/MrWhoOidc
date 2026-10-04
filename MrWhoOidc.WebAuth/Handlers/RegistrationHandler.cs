using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Models.DynamicRegistration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRegistrationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed partial class RegistrationHandler(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IOptions<AuthOptions> authOptions,
    IPlatformSettingsService platformSettingsService,
    IPlatformInitialAccessTokenService initialAccessTokenService,
    IPasswordHasher passwordHasher,
    IHttpClientFactory httpClientFactory,
    ILogger<RegistrationHandler> logger) : IRegistrationHandler
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    internal static readonly HashSet<string> SupportedGrantTypes = new(StringComparer.Ordinal)
    {
        "authorization_code",
        "refresh_token",
        "client_credentials",
        "urn:ietf:params:oauth:grant-type:token-exchange"
    };

    internal static readonly HashSet<string> SupportedResponseTypes = new(StringComparer.Ordinal)
    {
        "code"
    };

    // Same list discovery advertises as token_endpoint_auth_methods_supported.
    internal static readonly HashSet<string> SupportedAuthMethods = new(
        MrWhoOidc.WebAuth.Services.ClientAuthenticator.SupportedTokenEndpointAuthMethods,
        StringComparer.Ordinal);

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        // Check feature flag (compile-time / configuration enablement)
        if (!_authOptions.EnableDynamicClientRegistration)
        {
            logger.LogWarning("/register called but dynamic client registration is disabled");
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
                statusCode: 400);
        }

        // Check runtime toggle (platform setting)
        var platformSettings = await platformSettingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!platformSettings.DynamicClientRegistrationEnabled)
        {
            logger.LogWarning("/register called but dynamic client registration is disabled by platform settings");
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
                statusCode: 400);
        }

        // In Production, anonymous dynamic client registration is rejected by default:
        // an initial access token is required unless the operator explicitly opts out
        // via the Dcr:AllowAnonymousInProduction escape hatch. Development/Staging keep
        // the existing behavior (anonymous allowed while RequireInitialAccessToken is off).
        var environment = http.RequestServices?.GetService<IHostEnvironment>();
        var allowAnonymousInProduction = string.Equals(
            http.RequestServices?.GetService<IConfiguration>()?["Dcr:AllowAnonymousInProduction"],
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (environment?.IsProduction() == true && !_authOptions.RequireInitialAccessToken && !allowAnonymousInProduction)
        {
            logger.LogWarning("/register rejected anonymous dynamic client registration in production (RequireInitialAccessToken=false, Dcr:AllowAnonymousInProduction not set)");
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic Client Registration requires an initial access token in production. Set RequireInitialAccessToken=true or Dcr:AllowAnonymousInProduction=true to override." },
                statusCode: 403);
        }

            if (_authOptions.RequireInitialAccessToken)
            {
                var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
                if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.Json(
                        new { error = "invalid_token", error_description = "Initial access token required" },
                        statusCode: 401);
                }

                var initialToken = authHeader.Substring(7).Trim();
                if (string.IsNullOrWhiteSpace(initialToken) || !(await initialAccessTokenService.ValidateAsync(initialToken, http.RequestAborted).ConfigureAwait(false)))
                {
                    logger.LogWarning("/register invalid initial access token");
                    return Results.Json(
                        new { error = "invalid_token", error_description = "Invalid initial access token" },
                        statusCode: 401);
                }
            }

        var tenant = tenantAccessor.CurrentTenant;
        if (tenant == null || tenant.TenantId == Guid.Empty)
        {
            logger.LogWarning("/register tenant resolution failed");
            return Results.Json(
                new { error = "server_error", error_description = "Tenant resolution failed" },
                statusCode: 500);
        }

        var tenantId = tenant.TenantId;

        // Parse JSON request body
        ClientRegistrationRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ClientRegistrationRequest>();
            if (request == null)
            {
                return Results.Json(
                    new { error = "invalid_request", error_description = "Request body is required" },
                    statusCode: 400);
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "/register invalid JSON");
            return Results.Json(
                new { error = "invalid_request", error_description = "Invalid JSON in request body" },
                statusCode: 400);
        }

        // Validate redirect_uris (required)
        if (request.RedirectUris == null || request.RedirectUris.Count == 0)
        {
            return Results.Json(
                new { error = "invalid_redirect_uri", error_description = "At least one redirect_uri is required" },
                statusCode: 400);
        }

        foreach (var uri in request.RedirectUris)
        {
            var redirectError = DynamicClientMetadataValidator.ValidateRedirectUri(uri);
            if (redirectError != null)
            {
                return Results.Json(
                    new { error = "invalid_redirect_uri", error_description = redirectError },
                    statusCode: 400);
            }
        }

        foreach (var uri in request.PostLogoutRedirectUris ?? [])
        {
            var logoutRedirectError = DynamicClientMetadataValidator.ValidateRedirectUri(uri, "post_logout_redirect_uri");
            if (logoutRedirectError != null)
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = logoutRedirectError },
                    statusCode: 400);
            }
        }

        // Validate grant_types
        var grantTypes = request.GrantTypes ?? new List<string> { "authorization_code" };
        foreach (var gt in grantTypes)
        {
            if (!SupportedGrantTypes.Contains(gt))
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = $"Unsupported grant_type: {gt}" },
                    statusCode: 400);
            }
        }

        // Validate response_types
        var responseTypes = request.ResponseTypes ?? new List<string> { "code" };
        foreach (var rt in responseTypes)
        {
            if (!SupportedResponseTypes.Contains(rt))
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = $"Unsupported response_type: {rt}" },
                    statusCode: 400);
            }
        }

        // Validate token_endpoint_auth_method
        var authMethod = request.TokenEndpointAuthMethod ?? "client_secret_basic";
        if (!SupportedAuthMethods.Contains(authMethod))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = $"Unsupported token_endpoint_auth_method: {authMethod}" },
                statusCode: 400);
        }

        // Validate application_type
        var appType = request.ApplicationType ?? "web";
        if (appType != "web" && appType != "native")
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "application_type must be 'web' or 'native'" },
                statusCode: 400);
        }

        // Validate subject_type (if specified)
        if (!string.IsNullOrEmpty(request.SubjectType) &&
            request.SubjectType != "public" && request.SubjectType != "pairwise")
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "subject_type must be 'public' or 'pairwise'" },
                statusCode: 400);
        }

        // Validate software_id: RFC 7591 requires it to be a URI-style client identifier
        // (non-empty, at most 128 characters, restricted character set).
        if (!string.IsNullOrEmpty(request.SoftwareId) && !SoftwareIdRegex().IsMatch(request.SoftwareId))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "software_id must be a non-empty string of 1-128 characters matching [A-Za-z0-9._:-]" },
                statusCode: 400);
        }

        // Reject software_statement: accepted in request but not validated or enforced
        if (!string.IsNullOrEmpty(request.SoftwareStatement))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "software_statement is not supported" },
                statusCode: 400);
        }

        // Reject request_object crypto metadata: not enforced by this OP
        if (!string.IsNullOrEmpty(request.RequestObjectSigningAlg))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "request_object_signing_alg is not supported" },
                statusCode: 400);
        }
        if (!string.IsNullOrEmpty(request.RequestObjectEncryptionAlg))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "request_object_encryption_alg is not supported" },
                statusCode: 400);
        }
        if (!string.IsNullOrEmpty(request.RequestObjectEncryptionEnc))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "request_object_encryption_enc is not supported" },
                statusCode: 400);
        }

        // Reject initiate_login_uri: not implemented
        if (!string.IsNullOrEmpty(request.InitiateLoginUri))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "initiate_login_uri is not supported" },
                statusCode: 400);
        }
        if (request.RequestUris is { Count: > 0 })
        {
            foreach (var requestUri in request.RequestUris)
            {
                if (!Uri.TryCreate(requestUri, UriKind.Absolute, out _))
                {
                    return Results.Json(
                        new { error = "invalid_client_metadata", error_description = $"Invalid request_uri: {requestUri}" },
                        statusCode: 400);
                }
            }
        }

        // Enforce mutual exclusivity: jwks and jwks_uri must not both be present
        if (request.Jwks != null && !string.IsNullOrEmpty(request.JwksUri))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "jwks and jwks_uri are mutually exclusive" },
                statusCode: 400);
        }

        var keysError = DynamicClientMetadataValidator.ValidatePrivateKeyJwtKeys(authMethod, request.Jwks, request.JwksUri);
        if (keysError != null)
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = keysError },
                statusCode: 400);
        }

        var mtlsThumbprintsError = DynamicClientMetadataValidator.ResolveMtlsThumbprints(authMethod, request.Jwks, out var mtlsThumbprintsJson);
        if (mtlsThumbprintsError != null)
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = mtlsThumbprintsError },
                statusCode: 400);
        }

        // For pairwise clients, validate sector_identifier_uri (HTTPS + redirect URI containment check)
        if (request.SubjectType == "pairwise" && !string.IsNullOrEmpty(request.SectorIdentifierUri))
        {
            if (!Uri.TryCreate(request.SectorIdentifierUri, UriKind.Absolute, out var sectorUri) ||
                !string.Equals(sectorUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = "sector_identifier_uri must be an HTTPS URI" },
                    statusCode: 400);
            }

            try
            {
                var httpClient = httpClientFactory.CreateClient(MrWhoOidc.Auth.Services.SubjectIdentifiers.SectorIdentifierResolver.SafeHttpClientName);
                await SectorIdentifierUriValidator.ValidateAsync(
                    sectorUri,
                    request.RedirectUris,
                    httpClient,
                    http.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "/register sector_identifier_uri validation failed for pairwise client");
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = ex.Message },
                    statusCode: 400);
            }
        }

        // Validate default_max_age (must be a non-negative integer if provided)
        if (request.DefaultMaxAge.HasValue && request.DefaultMaxAge.Value < 0)
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "default_max_age must be a non-negative integer" },
                statusCode: 400);
        }

        // Get tenant's active signing algorithm for crypto validation
        var activeSigningAlg = await db.SigningKeys
            .Where(k => k.TenantId == tenantId)
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Alg)
            .FirstOrDefaultAsync();

        if (string.IsNullOrEmpty(activeSigningAlg))
        {
            logger.LogError("/register no active signing key for tenant {TenantId}", tenantId);
            return Results.Json(
                new { error = "server_error", error_description = "Server configuration error" },
                statusCode: 500);
        }

        // Validate id_token_signed_response_alg
        if (!string.IsNullOrEmpty(request.IdTokenSignedResponseAlg))
        {
            if (request.IdTokenSignedResponseAlg == "none")
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = "id_token_signed_response_alg 'none' is not supported" },
                    statusCode: 400);
            }

            if (request.IdTokenSignedResponseAlg != activeSigningAlg)
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = $"id_token_signed_response_alg must match tenant active signing algorithm: {activeSigningAlg}" },
                    statusCode: 400);
            }
        }

        // Validate id_token / userinfo encryption (only RSA-OAEP + A256CBC-HS512 is supported)
        var encryptionError =
            DynamicClientMetadataValidator.ValidateEncryption("id_token", request.IdTokenEncryptedResponseAlg, request.IdTokenEncryptedResponseEnc)
            ?? DynamicClientMetadataValidator.ValidateEncryption("userinfo", request.UserinfoEncryptedResponseAlg, request.UserinfoEncryptedResponseEnc);
        if (encryptionError != null)
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = encryptionError },
                statusCode: 400);
        }

        // Generate unique client_id
        var clientId = GenerateClientId();

        // Resolve tenant-configured realm for dynamic registration.
        // Null disables dynamic registration for this tenant.
        var dynamicRealmId = await GetDynamicClientRegistrationRealmIdAsync(tenantId);
        if (dynamicRealmId == null)
        {
            logger.LogWarning("/register called but tenant {TenantId} has no dynamic registration realm configured", tenantId);
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled for this tenant" },
                statusCode: 400);
        }

        var realmExists = await db.Realms
            .AnyAsync(r => r.TenantId == tenantId && r.Id == dynamicRealmId.Value);

        if (!realmExists)
        {
            logger.LogError("/register tenant {TenantId} configured dynamic registration realm {RealmId} does not exist", tenantId, dynamicRealmId);
            return Results.Json(
                new { error = "server_error", error_description = "Server configuration error" },
                statusCode: 500);
        }

        // Generate client_secret for confidential clients
        string? clientSecret = null;
        long clientSecretExpiresAt = 0; // 0 = never expires per RFC 7591

        var client = MapRequestToClient(request, clientId, tenantId, dynamicRealmId.Value, grantTypes, responseTypes, authMethod, appType);
        client.M2MMtlsThumbprintsJson = mtlsThumbprintsJson;

        if (authMethod is "client_secret_basic" or "client_secret_post")
        {
            clientSecret = GenerateClientSecret();
            var hashedSecret = passwordHasher.Hash(clientSecret);

            client.ClientSecrets = new List<ClientSecret>
            {
                new ClientSecret
                {
                    Id = Guid.NewGuid(),
                    ClientId = client.Id, // FK to Client.Id (Guid)
                    SecretHash = hashedSecret,
                    Description = "Auto-generated during dynamic registration",
                    CreatedAtUtc = DateTime.UtcNow,
                    ActivatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = null, // No expiry for dynamically registered clients
                    IsPrimary = true
                }
            };
        }

        db.Clients.Add(client);

        // Generate registration_access_token (RFC 7592)
        var registrationToken = GenerateRegistrationAccessToken();
        var tokenHash = HashRegistrationToken(registrationToken);

        // Store registration token in DB for future client configuration endpoint access
        DateTime? expiresAtUtc = null;
        if (_authOptions.RegistrationAccessTokenLifetimeSeconds > 0)
        {
            expiresAtUtc = DateTime.UtcNow.AddSeconds(_authOptions.RegistrationAccessTokenLifetimeSeconds);
        }

        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = clientId, // string client_id, not GUID
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAtUtc
        });

        await db.SaveChangesAsync();

        logger.LogInformation("Dynamically registered client {ClientId} in tenant {TenantId}", clientId, tenantId);

        // Build response
        var response = new ClientRegistrationResponse
        {
            ClientId = clientId,
            ClientSecret = clientSecret, // Return plaintext secret once (only time client sees it)
            ClientIdIssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ClientSecretExpiresAt = clientSecretExpiresAt,
            RegistrationAccessToken = registrationToken,
            RegistrationClientUri = $"{http.GetIssuer().TrimEnd('/')}/register/{clientId}",

            // Echo back all metadata
            RedirectUris = ClientConfigurationHandler.ParseStringList(client.AllowedLoginRedirectUrisJson) ?? new List<string>(),
            TokenEndpointAuthMethod = client.TokenEndpointAuthMethod,
            GrantTypes = ClientConfigurationHandler.ParseStringList(client.GrantTypesJson),
            ResponseTypes = ClientConfigurationHandler.ParseStringList(client.ResponseTypesJson),
            ClientName = client.ClientName,
            ClientUri = client.ClientUri,
            LogoUri = client.LogoUri,
            Scope = client.Scope,
            Contacts = ClientConfigurationHandler.ParseStringList(client.ContactsJson),
            TosUri = client.TosUri,
            PolicyUri = client.PolicyUri,
            JwksUri = client.PublicJwksUri,
            Jwks = !string.IsNullOrWhiteSpace(client.PublicJwksJson) ? JsonSerializer.Deserialize<object>(client.PublicJwksJson) : null,
            SoftwareId = client.SoftwareId,
            SoftwareVersion = client.SoftwareVersion,
            ApplicationType = client.ApplicationType,
            SectorIdentifierUri = client.SectorIdentifierUri,
            SubjectType = client.SubjectType,
            IdTokenSignedResponseAlg = client.IdTokenSignedResponseAlg,
            IdTokenEncryptedResponseAlg = client.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = client.IdTokenEncryptedResponseEnc,
            UserinfoSignedResponseAlg = client.UserInfoSignedResponseAlg,
            UserinfoEncryptedResponseAlg = client.UserInfoEncryptedResponseAlg,
            UserinfoEncryptedResponseEnc = client.UserInfoEncryptedResponseEnc,
            RequestUris = request.RequestUris,
            DefaultMaxAge = client.DefaultMaxAge,
            RequireAuthTime = client.RequireAuthTime,
            DefaultAcrValues = ClientConfigurationHandler.ParseStringList(client.DefaultAcrValuesJson),
            BackchannelLogoutUri = client.BackChannelLogoutUri,
            BackchannelLogoutSessionRequired = client.BackChannelLogoutSessionRequired,
            FrontchannelLogoutUri = client.FrontChannelLogoutUri,
            FrontchannelLogoutSessionRequired = client.FrontChannelLogoutSessionRequired,
            PostLogoutRedirectUris = ClientConfigurationHandler.ParseStringList(client.AllowedLogoutRedirectUrisJson)
        };

        return Results.Json(response, statusCode: 201);
    }

    private static Client MapRequestToClient(
        ClientRegistrationRequest request,
        string clientId,
        Guid tenantId,
        Guid realmId,
        List<string> grantTypes,
        List<string> responseTypes,
        string authMethod,
        string appType)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            TenantId = tenantId,
            RealmId = realmId,
            AutoApprovalMode = AutoApprovalMode.All,
            RequireConsent = true // Default to requiring consent for dynamic clients
        };

        ApplyClientMetadata(client, request, grantTypes, responseTypes, authMethod, appType);
        return client;
    }

    /// <summary>
    /// Writes every client-supplied metadata field onto <paramref name="client"/>; omitted fields get
    /// their registration defaults. Shared by registration and RFC 7592 PUT, which replaces the whole
    /// client metadata. Server-managed fields (ids, tenant, realm, secrets, consent policy) are untouched.
    /// </summary>
    internal static void ApplyClientMetadata(
        Client client,
        ClientRegistrationRequest request,
        List<string> grantTypes,
        List<string> responseTypes,
        string authMethod,
        string appType)
    {
        client.ClientName = request.ClientName ?? $"Dynamic Client {client.ClientId}";
        client.TokenEndpointAuthMethod = authMethod;
        client.GrantTypesJson = JsonSerializer.Serialize(grantTypes);
        client.ResponseTypesJson = JsonSerializer.Serialize(responseTypes);
        client.ClientUri = request.ClientUri;
        client.LogoUri = request.LogoUri;
        client.Scope = request.Scope;
        client.ContactsJson = request.Contacts is { Count: > 0 } ? JsonSerializer.Serialize(request.Contacts) : null;
        client.TosUri = request.TosUri;
        client.PolicyUri = request.PolicyUri;
        client.SoftwareId = request.SoftwareId;
        client.SoftwareVersion = request.SoftwareVersion;
        client.ApplicationType = appType;
        client.SubjectType = request.SubjectType ?? "public";
        client.SectorIdentifierUri = request.SectorIdentifierUri;
        client.RequirePkce = appType == "native"; // Require PKCE for native apps
        client.PublicJwksUri = request.JwksUri;
        client.PublicJwksJson = request.Jwks != null ? JsonSerializer.Serialize(request.Jwks) : null;
        client.IdTokenSignedResponseAlg = request.IdTokenSignedResponseAlg;
        client.IdTokenEncryptedResponseAlg = request.IdTokenEncryptedResponseAlg;
        client.IdTokenEncryptedResponseEnc = request.IdTokenEncryptedResponseEnc;
        client.UserInfoSignedResponseAlg = request.UserinfoSignedResponseAlg;
        client.UserInfoEncryptedResponseAlg = request.UserinfoEncryptedResponseAlg;
        client.UserInfoEncryptedResponseEnc = request.UserinfoEncryptedResponseEnc;
        client.BackChannelLogoutUri = request.BackchannelLogoutUri;
        client.BackChannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired ?? false;
        client.FrontChannelLogoutUri = request.FrontchannelLogoutUri;
        client.FrontChannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired ?? false;
        client.DefaultMaxAge = request.DefaultMaxAge;
        client.RequireAuthTime = request.RequireAuthTime;
        client.DefaultAcrValuesJson = request.DefaultAcrValues is { Count: > 0 }
            ? JsonSerializer.Serialize(request.DefaultAcrValues)
            : null;
        client.AllowedLoginRedirectUrisJson = request.RedirectUris is { Count: > 0 }
            ? JsonSerializer.Serialize(request.RedirectUris)
            : null;
        client.AllowedLogoutRedirectUrisJson = request.PostLogoutRedirectUris is { Count: > 0 }
            ? JsonSerializer.Serialize(request.PostLogoutRedirectUris)
            : null;
    }

    private static string GenerateClientId()
    {
        // Generate cryptographically secure random client_id
        return $"dyn_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    private static string GenerateClientSecret()
    {
        // Generate cryptographically secure random client_secret
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
    }

    internal static string GenerateRegistrationAccessToken()
    {
        // Generate cryptographically secure registration access token
        return $"rat_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace("+", "-").Replace("/", "_").TrimEnd('=')}";
    }

    private async Task<Guid?> GetDynamicClientRegistrationRealmIdAsync(Guid tenantId)
    {
        var settingsJson = await db.Tenants
            .Where(t => t.Id == tenantId)
            .Select(t => t.SettingsJson)
            .FirstOrDefaultAsync();

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson);
            return settings?.Auth?.DynamicClientRegistrationRealmId;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to deserialize tenant settings JSON for tenant {TenantId}", tenantId);
            return null;
        }
    }

    private static string HashRegistrationToken(string token)
    {
        // SHA-256 hash of token for storage (similar to how we store secrets)
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// RFC 7591 software_id: a URI-style client identifier of 1-128 characters from [A-Za-z0-9._:-].
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,128}$")]
    private static partial System.Text.RegularExpressions.Regex SoftwareIdRegex();
}
