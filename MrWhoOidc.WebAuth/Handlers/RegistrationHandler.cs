using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.SubjectIdentifiers;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Models.DynamicRegistration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IRegistrationHandler
{
    Task<IResult> HandleAsync(HttpContext http);
}

public sealed class RegistrationHandler(
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

    internal static readonly HashSet<string> SupportedAuthMethods = new(StringComparer.Ordinal)
    {
        "client_secret_basic",
        "client_secret_post",
        "private_key_jwt",
        "none" // for public clients
    };

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

        // Always require an initial access token for POST /register.
        // Tokens are managed via platform admin UI and stored as hashes in the database.
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
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri))
            {
                return Results.Json(
                    new { error = "invalid_redirect_uri", error_description = $"Invalid redirect_uri: {uri}" },
                    statusCode: 400);
            }

            // RFC 8252: Native apps should not use http (except localhost)
            if (parsedUri.Scheme == "http" && !IsLocalhost(parsedUri.Host))
            {
                return Results.Json(
                    new { error = "invalid_redirect_uri", error_description = "http redirect_uris are only allowed for localhost" },
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

        // Reject initiate_login_uri and request_uris: not implemented
        if (!string.IsNullOrEmpty(request.InitiateLoginUri))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "initiate_login_uri is not supported" },
                statusCode: 400);
        }
        if (request.RequestUris != null && request.RequestUris.Count > 0)
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "request_uris is not supported" },
                statusCode: 400);
        }

        // Enforce mutual exclusivity: jwks and jwks_uri must not both be present
        if (request.Jwks != null && !string.IsNullOrEmpty(request.JwksUri))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "jwks and jwks_uri are mutually exclusive" },
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
                var httpClient = httpClientFactory.CreateClient("SectorIdentifierValidator");
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

        // Validate id_token encryption (if specified, enforce RSA-OAEP + A256CBC-HS512)
        if (!string.IsNullOrEmpty(request.IdTokenEncryptedResponseAlg))
        {
            if (request.IdTokenEncryptedResponseAlg != "RSA-OAEP")
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = "id_token_encrypted_response_alg must be 'RSA-OAEP'" },
                    statusCode: 400);
            }

            var enc = request.IdTokenEncryptedResponseEnc ?? "A256CBC-HS512";
            if (enc != "A256CBC-HS512")
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = "id_token_encrypted_response_enc must be 'A256CBC-HS512'" },
                    statusCode: 400);
            }
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

        if (authMethod != "none" && authMethod != "private_key_jwt")
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
            RegistrationClientUri = $"{http.Request.Scheme}://{http.Request.Host}/register/{clientId}",

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
            ClientName = request.ClientName ?? $"Dynamic Client {clientId}",
            TokenEndpointAuthMethod = authMethod,
            GrantTypesJson = JsonSerializer.Serialize(grantTypes),
            ResponseTypesJson = JsonSerializer.Serialize(responseTypes),
            ClientUri = request.ClientUri,
            LogoUri = request.LogoUri,
            Scope = request.Scope,
            ContactsJson = request.Contacts != null && request.Contacts.Count > 0 ? JsonSerializer.Serialize(request.Contacts) : null,
            TosUri = request.TosUri,
            PolicyUri = request.PolicyUri,
            SoftwareId = request.SoftwareId,
            SoftwareVersion = request.SoftwareVersion,
            ApplicationType = appType,
            SubjectType = request.SubjectType ?? "public",
            SectorIdentifierUri = request.SectorIdentifierUri,
            RequireConsent = true, // Default to requiring consent for dynamic clients
            RequirePkce = appType == "native", // Require PKCE for native apps
            PublicJwksUri = request.JwksUri,
            PublicJwksJson = request.Jwks != null ? JsonSerializer.Serialize(request.Jwks) : null,
            IdTokenSignedResponseAlg = request.IdTokenSignedResponseAlg,
            IdTokenEncryptedResponseAlg = request.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = request.IdTokenEncryptedResponseEnc,
            UserInfoSignedResponseAlg = request.UserinfoSignedResponseAlg,
            UserInfoEncryptedResponseAlg = request.UserinfoEncryptedResponseAlg,
            UserInfoEncryptedResponseEnc = request.UserinfoEncryptedResponseEnc,
            BackChannelLogoutUri = request.BackchannelLogoutUri,
            BackChannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired ?? false,
            FrontChannelLogoutUri = request.FrontchannelLogoutUri,
            FrontChannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired ?? false,
            DefaultMaxAge = request.DefaultMaxAge,
            RequireAuthTime = request.RequireAuthTime,
            DefaultAcrValuesJson = request.DefaultAcrValues != null && request.DefaultAcrValues.Count > 0
                ? JsonSerializer.Serialize(request.DefaultAcrValues)
                : null
        };

        // Store redirect_uris in AllowedLoginRedirectUrisJson
        if (request.RedirectUris != null && request.RedirectUris.Count > 0)
        {
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris);
        }

        // Store post_logout_redirect_uris in AllowedLogoutRedirectUrisJson
        if (request.PostLogoutRedirectUris != null && request.PostLogoutRedirectUris.Count > 0)
        {
            client.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(request.PostLogoutRedirectUris);
        }

        return client;
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

    private static string GenerateRegistrationAccessToken()
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

    private static bool IsLocalhost(string host)
    {
        return host == "localhost" ||
               host == "127.0.0.1" ||
               host == "[::1]" ||
               host.StartsWith("127.") ||
               host.StartsWith("[::ffff:127.");
    }
}
