using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Settings;
using MrWhoOidc.WebAuth.Models.DynamicRegistration;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.Handlers;

/// <summary>
/// RFC 7592 - OAuth 2.0 Dynamic Client Registration Management Protocol
/// Handles GET, PUT, DELETE on /register/{client_id} for client configuration management.
/// </summary>
public interface IClientConfigurationHandler
{
    /// <summary>
    /// GET /register/{client_id} - Read client configuration
    /// </summary>
    Task<IResult> GetClientAsync(HttpContext http, string clientId);

    /// <summary>
    /// PUT /register/{client_id} - Update client configuration
    /// </summary>
    Task<IResult> UpdateClientAsync(HttpContext http, string clientId);

    /// <summary>
    /// DELETE /register/{client_id} - Delete client registration
    /// </summary>
    Task<IResult> DeleteClientAsync(HttpContext http, string clientId);
}

public sealed class ClientConfigurationHandler(
    AuthDbContext db,
    ITenantAccessor tenantAccessor,
    IOptions<AuthOptions> authOptions,
    IPlatformSettingsService platformSettingsService,
    ILogger<ClientConfigurationHandler> logger) : IClientConfigurationHandler
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    public async Task<IResult> GetClientAsync(HttpContext http, string clientId)
    {
        // Check feature flags
        var featureError = await CheckFeatureFlagsAsync("GET", http.RequestAborted).ConfigureAwait(false);
        if (featureError != null) return featureError;

        var (client, error) = await ValidateAccessAndGetClientAsync(http, clientId);
        if (error != null) return error;
        if (client == null) return Results.NotFound();

        // Build response with current client metadata
        var response = BuildClientResponse(client, http);
        return Results.Json(response, statusCode: 200);
    }

    public async Task<IResult> UpdateClientAsync(HttpContext http, string clientId)
    {
        // Check feature flags
        var featureError = await CheckFeatureFlagsAsync("PUT", http.RequestAborted).ConfigureAwait(false);
        if (featureError != null) return featureError;

        var (client, error) = await ValidateAccessAndGetClientAsync(http, clientId);
        if (error != null) return error;
        if (client == null) return Results.NotFound();

        // Parse updated metadata
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
            logger.LogWarning(ex, "PUT /register/{ClientId} invalid JSON", clientId);
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

            if (parsedUri.Scheme == "http" && !IsLocalhost(parsedUri.Host))
            {
                return Results.Json(
                    new { error = "invalid_redirect_uri", error_description = "http redirect_uris are only allowed for localhost" },
                    statusCode: 400);
            }
        }

        var grantTypes = request.GrantTypes ?? ParseStringList(client.GrantTypesJson) ?? new List<string> { "authorization_code" };
        foreach (var grantType in grantTypes)
        {
            if (!RegistrationHandler.SupportedGrantTypes.Contains(grantType))
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = $"Unsupported grant_type: {grantType}" },
                    statusCode: 400);
            }
        }

        var responseTypes = request.ResponseTypes ?? ParseStringList(client.ResponseTypesJson) ?? new List<string> { "code" };
        foreach (var responseType in responseTypes)
        {
            if (!RegistrationHandler.SupportedResponseTypes.Contains(responseType))
            {
                return Results.Json(
                    new { error = "invalid_client_metadata", error_description = $"Unsupported response_type: {responseType}" },
                    statusCode: 400);
            }
        }

        var authMethod = request.TokenEndpointAuthMethod ?? client.TokenEndpointAuthMethod ?? "client_secret_basic";
        if (!RegistrationHandler.SupportedAuthMethods.Contains(authMethod))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = $"Unsupported token_endpoint_auth_method: {authMethod}" },
                statusCode: 400);
        }

        var appType = request.ApplicationType ?? client.ApplicationType ?? "web";
        if (!string.Equals(appType, "web", StringComparison.Ordinal) && !string.Equals(appType, "native", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "application_type must be 'web' or 'native'" },
                statusCode: 400);
        }

        var subjectType = request.SubjectType ?? client.SubjectType;
        if (!string.IsNullOrEmpty(subjectType)
            && !string.Equals(subjectType, "public", StringComparison.Ordinal)
            && !string.Equals(subjectType, "pairwise", StringComparison.Ordinal))
        {
            return Results.Json(
                new { error = "invalid_client_metadata", error_description = "subject_type must be 'public' or 'pairwise'" },
                statusCode: 400);
        }

        // Update client metadata
        // Reject unsupported metadata fields (same contract as POST /register)
        if (!string.IsNullOrEmpty(request.SoftwareStatement))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "software_statement is not supported" }, statusCode: 400);
        if (!string.IsNullOrEmpty(request.RequestObjectSigningAlg))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "request_object_signing_alg is not supported" }, statusCode: 400);
        if (!string.IsNullOrEmpty(request.RequestObjectEncryptionAlg))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "request_object_encryption_alg is not supported" }, statusCode: 400);
        if (!string.IsNullOrEmpty(request.RequestObjectEncryptionEnc))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "request_object_encryption_enc is not supported" }, statusCode: 400);
        if (!string.IsNullOrEmpty(request.InitiateLoginUri))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "initiate_login_uri is not supported" }, statusCode: 400);
        if (request.RequestUris != null && request.RequestUris.Count > 0)
            return Results.Json(new { error = "invalid_client_metadata", error_description = "request_uris is not supported" }, statusCode: 400);
        if (request.Jwks != null && !string.IsNullOrEmpty(request.JwksUri))
            return Results.Json(new { error = "invalid_client_metadata", error_description = "jwks and jwks_uri are mutually exclusive" }, statusCode: 400);
        if (request.DefaultMaxAge.HasValue && request.DefaultMaxAge.Value < 0)
            return Results.Json(new { error = "invalid_client_metadata", error_description = "default_max_age must be a non-negative integer" }, statusCode: 400);

        client.ClientName = request.ClientName ?? client.ClientName;
        client.TokenEndpointAuthMethod = authMethod;
        client.GrantTypesJson = JsonSerializer.Serialize(grantTypes);
        client.ResponseTypesJson = JsonSerializer.Serialize(responseTypes);
        client.ClientUri = request.ClientUri ?? client.ClientUri;
        client.LogoUri = request.LogoUri ?? client.LogoUri;
        client.Scope = request.Scope ?? client.Scope;
        if (request.Contacts != null)
        {
            client.ContactsJson = request.Contacts.Count > 0 ? JsonSerializer.Serialize(request.Contacts) : null;
        }
        client.TosUri = request.TosUri ?? client.TosUri;
        client.PolicyUri = request.PolicyUri ?? client.PolicyUri;
        client.SoftwareId = request.SoftwareId ?? client.SoftwareId;
        client.SoftwareVersion = request.SoftwareVersion ?? client.SoftwareVersion;
        client.ApplicationType = appType;
        client.SubjectType = subjectType ?? client.SubjectType;
        client.SectorIdentifierUri = request.SectorIdentifierUri;
        if (request.Jwks != null)
        {
            client.PublicJwksJson = JsonSerializer.Serialize(request.Jwks);
            client.PublicJwksUri = null;
        }
        else if (!string.IsNullOrWhiteSpace(request.JwksUri))
        {
            client.PublicJwksUri = request.JwksUri;
            client.PublicJwksJson = null;
        }
        client.IdTokenSignedResponseAlg = request.IdTokenSignedResponseAlg;
        client.IdTokenEncryptedResponseAlg = request.IdTokenEncryptedResponseAlg;
        client.IdTokenEncryptedResponseEnc = request.IdTokenEncryptedResponseEnc;
        client.UserInfoSignedResponseAlg = request.UserinfoSignedResponseAlg;
        client.UserInfoEncryptedResponseAlg = request.UserinfoEncryptedResponseAlg;
        client.UserInfoEncryptedResponseEnc = request.UserinfoEncryptedResponseEnc;
        client.BackChannelLogoutUri = request.BackchannelLogoutUri;
        client.BackChannelLogoutSessionRequired = request.BackchannelLogoutSessionRequired ?? client.BackChannelLogoutSessionRequired;
        client.FrontChannelLogoutUri = request.FrontchannelLogoutUri;
        client.FrontChannelLogoutSessionRequired = request.FrontchannelLogoutSessionRequired ?? client.FrontChannelLogoutSessionRequired;
        if (request.DefaultMaxAge.HasValue)
        {
            client.DefaultMaxAge = request.DefaultMaxAge;
        }
        if (request.RequireAuthTime.HasValue)
        {
            client.RequireAuthTime = request.RequireAuthTime;
        }
        if (request.DefaultAcrValues != null)
        {
            client.DefaultAcrValuesJson = request.DefaultAcrValues.Count > 0
                ? JsonSerializer.Serialize(request.DefaultAcrValues)
                : null;
        }
        client.RequirePkce = string.Equals(appType, "native", StringComparison.Ordinal);

        // Update redirect URIs
        if (request.RedirectUris.Count > 0)
        {
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris);
        }

        // Update post_logout_redirect_uris
        if (request.PostLogoutRedirectUris != null)
        {
            client.AllowedLogoutRedirectUrisJson = request.PostLogoutRedirectUris.Count > 0
                ? JsonSerializer.Serialize(request.PostLogoutRedirectUris)
                : null;
        }

        await db.SaveChangesAsync();

        logger.LogInformation("Updated client configuration for {ClientId}", clientId);

        // Build response with updated client metadata
        var response = BuildClientResponse(client, http);
        return Results.Json(response, statusCode: 200);
    }

    public async Task<IResult> DeleteClientAsync(HttpContext http, string clientId)
    {
        // Check feature flags
        var featureError = await CheckFeatureFlagsAsync("DELETE", http.RequestAborted).ConfigureAwait(false);
        if (featureError != null) return featureError;

        var (client, error) = await ValidateAccessAndGetClientAsync(http, clientId);
        if (error != null) return error;
        if (client == null) return Results.NotFound();

        // Delete associated registration tokens
        var tokens = await db.DynamicRegistrationTokens
            .Where(t => t.ClientId == clientId)
            .ToListAsync();
        db.DynamicRegistrationTokens.RemoveRange(tokens);

        // Delete associated client secrets
        var secrets = await db.ClientSecrets
            .Where(s => s.ClientId == client.Id)
            .ToListAsync();
        db.ClientSecrets.RemoveRange(secrets);

        // Delete associated client scopes
        var scopes = await db.ClientScopes
            .Where(s => s.ClientId == client.Id)
            .ToListAsync();
        db.ClientScopes.RemoveRange(scopes);

        // Delete the client
        db.Clients.Remove(client);

        await db.SaveChangesAsync();

        logger.LogInformation("Deleted dynamically registered client {ClientId}", clientId);

        return Results.NoContent();
    }

    private async Task<(Client? client, IResult? error)> ValidateAccessAndGetClientAsync(HttpContext http, string clientId)
    {
        var tenant = tenantAccessor.CurrentTenant;
        if (tenant == null || tenant.TenantId == Guid.Empty)
        {
            logger.LogWarning("/register/{ClientId} tenant resolution failed", clientId);
            return (null, Results.Json(
                new { error = "server_error", error_description = "Tenant resolution failed" },
                statusCode: 500));
        }

        // Extract registration_access_token from Authorization header
        var authHeader = http.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Results.Json(
                new { error = "invalid_token", error_description = "Missing or invalid Authorization header" },
                statusCode: 401));
        }

        var token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return (null, Results.Json(
                new { error = "invalid_token", error_description = "Empty bearer token" },
                statusCode: 401));
        }

        // Hash the token to compare with stored hash
        var tokenHash = HashRegistrationToken(token);

        // Verify the registration access token exists and matches the client
        var regToken = await db.DynamicRegistrationTokens
            .FirstOrDefaultAsync(t => t.ClientId == clientId && t.TokenHash == tokenHash);

        if (regToken == null)
        {
            logger.LogWarning("/register/{ClientId} invalid registration access token", clientId);
            return (null, Results.Json(
                new { error = "invalid_token", error_description = "Invalid registration access token" },
                statusCode: 401));
        }

        // Check token expiry if set
        if (regToken.ExpiresAt.HasValue && regToken.ExpiresAt.Value < DateTime.UtcNow)
        {
            logger.LogWarning("/register/{ClientId} registration access token expired", clientId);
            return (null, Results.Json(
                new { error = "invalid_token", error_description = "Registration access token has expired" },
                statusCode: 401));
        }

        // Get the client
        var client = await db.Clients
            .FirstOrDefaultAsync(c => c.ClientId == clientId && c.TenantId == tenant.TenantId);

        if (client == null)
        {
            logger.LogWarning("/register/{ClientId} client not found", clientId);
            return (null, Results.Json(
                new { error = "invalid_client", error_description = "Client not found" },
                statusCode: 404));
        }

        return (client, null);
    }

    private ClientRegistrationResponse BuildClientResponse(Client client, HttpContext http)
    {
        // Parse stored JSON arrays
        List<string> redirectUris = new();
        if (!string.IsNullOrEmpty(client.AllowedLoginRedirectUrisJson))
        {
            redirectUris = JsonSerializer.Deserialize<List<string>>(client.AllowedLoginRedirectUrisJson) ?? new List<string>();
        }

        var postLogoutUris = ParseStringList(client.AllowedLogoutRedirectUrisJson);

        return new ClientRegistrationResponse
        {
            ClientId = client.ClientId,
            ClientSecret = null, // Never return client_secret on GET/PUT
            ClientIdIssuedAt = 0, // We don't track this
            ClientSecretExpiresAt = 0, // 0 = never expires
            RegistrationClientUri = $"{http.Request.Scheme}://{http.Request.Host}/register/{client.ClientId}"!,
            RedirectUris = redirectUris,
            TokenEndpointAuthMethod = client.TokenEndpointAuthMethod,
            GrantTypes = ParseStringList(client.GrantTypesJson),
            ResponseTypes = ParseStringList(client.ResponseTypesJson),
            ClientName = client.ClientName,
            ClientUri = client.ClientUri,
            LogoUri = client.LogoUri,
            Scope = client.Scope,
            Contacts = ParseStringList(client.ContactsJson),
            TosUri = client.TosUri,
            PolicyUri = client.PolicyUri,
            SubjectType = client.SubjectType,
            ApplicationType = client.ApplicationType,
            SectorIdentifierUri = client.SectorIdentifierUri,
            JwksUri = client.PublicJwksUri,
            Jwks = !string.IsNullOrEmpty(client.PublicJwksJson)
                ? JsonSerializer.Deserialize<object>(client.PublicJwksJson)
                : null,
            SoftwareId = client.SoftwareId,
            SoftwareVersion = client.SoftwareVersion,
            IdTokenSignedResponseAlg = client.IdTokenSignedResponseAlg,
            IdTokenEncryptedResponseAlg = client.IdTokenEncryptedResponseAlg,
            IdTokenEncryptedResponseEnc = client.IdTokenEncryptedResponseEnc,
            UserinfoSignedResponseAlg = client.UserInfoSignedResponseAlg,
            UserinfoEncryptedResponseAlg = client.UserInfoEncryptedResponseAlg,
            UserinfoEncryptedResponseEnc = client.UserInfoEncryptedResponseEnc,
            BackchannelLogoutUri = client.BackChannelLogoutUri,
            BackchannelLogoutSessionRequired = client.BackChannelLogoutSessionRequired,
            FrontchannelLogoutUri = client.FrontChannelLogoutUri,
            FrontchannelLogoutSessionRequired = client.FrontChannelLogoutSessionRequired,
            PostLogoutRedirectUris = postLogoutUris,
            DefaultMaxAge = client.DefaultMaxAge,
            RequireAuthTime = client.RequireAuthTime,
            DefaultAcrValues = !string.IsNullOrWhiteSpace(client.DefaultAcrValuesJson)
                ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(client.DefaultAcrValuesJson)
                : null
        };
    }

    internal static List<string>? ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<string>>(json);
    }

    private static string HashRegistrationToken(string token)
    {
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

    private async Task<IResult?> CheckFeatureFlagsAsync(string method, CancellationToken ct)
    {
        if (!_authOptions.EnableDynamicClientRegistration)
        {
            logger.LogWarning("{Method} /register/{{clientId}} called but dynamic client registration is disabled", method);
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
                statusCode: 400);
        }

        var platformSettings = await platformSettingsService.GetSettingsAsync().ConfigureAwait(false);
        if (!platformSettings.DynamicClientRegistrationEnabled)
        {
            logger.LogWarning("{Method} /register/{{clientId}} called but dynamic client registration is disabled by platform settings", method);
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
                statusCode: 400);
        }

        var dcrRealmId = await GetDynamicClientRegistrationRealmIdAsync(tenantAccessor.CurrentTenant?.TenantId, ct).ConfigureAwait(false);
        if (dcrRealmId == null)
        {
            logger.LogWarning("{Method} /register/{{clientId}} called but tenant has no dynamic registration realm configured", method);
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled for this tenant" },
                statusCode: 400);
        }

        if (!_authOptions.EnableClientConfigurationEndpoint)
        {
            logger.LogWarning("{Method} /register/{{clientId}} called but client configuration endpoint is disabled", method);
            return Results.Json(
                new { error = "invalid_request", error_description = "Client configuration management is not enabled" },
                statusCode: 400);
        }

        return null;
    }

    private async Task<Guid?> GetDynamicClientRegistrationRealmIdAsync(Guid? tenantId, CancellationToken ct)
    {
        if (tenantId is null || tenantId.Value == Guid.Empty)
        {
            return null;
        }

        var settingsJson = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == tenantId.Value)
            .Select(t => t.SettingsJson)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<TenantSettings>(settingsJson);
            return settings?.Auth?.DynamicClientRegistrationRealmId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
