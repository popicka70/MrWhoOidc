using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
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
    ILogger<RegistrationHandler> logger) : IRegistrationHandler
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    private static readonly List<string> SupportedGrantTypes = new()
    {
        "authorization_code",
        "refresh_token",
        "client_credentials",
        "urn:ietf:params:oauth:grant-type:token-exchange"
    };

    private static readonly List<string> SupportedResponseTypes = new()
    {
        "code"
    }; 

    private static readonly List<string> SupportedAuthMethods = new()
    {
        "client_secret_basic",
        "client_secret_post",
        "private_key_jwt",
        "none" // for public clients
    };

    public async Task<IResult> HandleAsync(HttpContext http)
    {
        // Check feature flag
        if (!_authOptions.EnableDynamicClientRegistration)
        {
            logger.LogWarning("/register called but dynamic client registration is disabled");
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
                statusCode: 400);
        }

        // Check initial access token if required
        if (_authOptions.RequireInitialAccessToken)
        {
            var authHeader = http.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(
                    new { error = "invalid_token", error_description = "Initial access token required" },
                    statusCode: 401);
            }

            var initialToken = authHeader.Substring(7);
            var initialTokenHash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(initialToken)));

            if (!_authOptions.InitialAccessTokenHashes.Contains(initialTokenHash, StringComparer.Ordinal))
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

        // Get or create default realm for this tenant
        var defaultRealm = await db.Realms
            .FirstOrDefaultAsync(r => r.TenantId == tenantId);

        if (defaultRealm == null)
        {
            logger.LogError("/register no realm found for tenant {TenantId}", tenantId);
            return Results.Json(
                new { error = "server_error", error_description = "Server configuration error" },
                statusCode: 500);
        }

        // Generate client_secret for confidential clients
        string? clientSecret = null;
        long clientSecretExpiresAt = 0; // 0 = never expires per RFC 7591
        
        var client = MapRequestToClient(request, clientId, tenantId, defaultRealm.Id, grantTypes, responseTypes, authMethod, appType);

        if (authMethod != "none" && authMethod != "private_key_jwt")
        {
            clientSecret = GenerateClientSecret();
            var hashedSecret = BCrypt.Net.BCrypt.HashPassword(clientSecret, workFactor: 12);
            
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
        db.DynamicRegistrationTokens.Add(new DynamicRegistrationToken
        {
            Id = Guid.NewGuid().ToString(),
            ClientId = clientId, // string client_id, not GUID
            TokenHash = tokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = null // No expiry
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
            RedirectUris = request.RedirectUris,
            TokenEndpointAuthMethod = authMethod,
            GrantTypes = grantTypes,
            ResponseTypes = responseTypes,
            ClientName = request.ClientName,
            ClientUri = request.ClientUri,
            LogoUri = request.LogoUri,
            Scope = request.Scope,
            Contacts = request.Contacts,
            TosUri = request.TosUri,
            PolicyUri = request.PolicyUri,
            JwksUri = request.JwksUri,
            Jwks = request.Jwks,
            SoftwareId = request.SoftwareId,
            SoftwareVersion = request.SoftwareVersion,
            ApplicationType = appType,
            SectorIdentifierUri = request.SectorIdentifierUri,
            SubjectType = request.SubjectType,
            IdTokenSignedResponseAlg = request.IdTokenSignedResponseAlg,
            IdTokenEncryptedResponseAlg = request.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = request.IdTokenEncryptedResponseEnc,
            UserinfoSignedResponseAlg = request.UserinfoSignedResponseAlg,
            UserinfoEncryptedResponseAlg = request.UserinfoEncryptedResponseAlg,
            UserinfoEncryptedResponseEnc = request.UserinfoEncryptedResponseEnc,
            RequestObjectSigningAlg = request.RequestObjectSigningAlg,
            RequestObjectEncryptionAlg = request.RequestObjectEncryptionAlg,
            RequestObjectEncryptionEnc = request.RequestObjectEncryptionEnc,
            DefaultMaxAge = request.DefaultMaxAge,
            RequireAuthTime = request.RequireAuthTime,
            DefaultAcrValues = request.DefaultAcrValues,
            InitiateLoginUri = request.InitiateLoginUri,
            RequestUris = request.RequestUris,
            BackchannelLogoutUri = request.BackchannelLogoutUri,
            BackchannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired,
            FrontchannelLogoutUri = request.FrontchannelLogoutUri,
            FrontchannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired,
            PostLogoutRedirectUris = request.PostLogoutRedirectUris
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
            FrontChannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired ?? false
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
