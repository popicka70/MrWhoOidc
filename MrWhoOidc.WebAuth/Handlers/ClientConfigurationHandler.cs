using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
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
    ILogger<ClientConfigurationHandler> logger) : IClientConfigurationHandler
{
    private readonly AuthOptions _authOptions = authOptions.Value;

    public async Task<IResult> GetClientAsync(HttpContext http, string clientId)
    {
        // Check feature flags
        var featureError = CheckFeatureFlags("GET");
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
        var featureError = CheckFeatureFlags("PUT");
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

        // Update client metadata
        client.ClientName = request.ClientName ?? client.ClientName;
        client.SubjectType = request.SubjectType ?? client.SubjectType;
        client.SectorIdentifierUri = request.SectorIdentifierUri;
        client.PublicJwksUri = request.JwksUri;
        client.PublicJwksJson = request.Jwks != null ? JsonSerializer.Serialize(request.Jwks) : client.PublicJwksJson;
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

        // Update redirect URIs
        if (request.RedirectUris.Count > 0)
        {
            client.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(request.RedirectUris);
        }

        // Update post_logout_redirect_uris
        if (request.PostLogoutRedirectUris != null && request.PostLogoutRedirectUris.Count > 0)
        {
            client.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(request.PostLogoutRedirectUris);
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
        var featureError = CheckFeatureFlags("DELETE");
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
        var redirectUris = !string.IsNullOrEmpty(client.AllowedLoginRedirectUrisJson)
            ? JsonSerializer.Deserialize<List<string>>(client.AllowedLoginRedirectUrisJson)
            : new List<string>();

        var postLogoutUris = !string.IsNullOrEmpty(client.AllowedLogoutRedirectUrisJson)
            ? JsonSerializer.Deserialize<List<string>>(client.AllowedLogoutRedirectUrisJson)
            : null;

        return new ClientRegistrationResponse
        {
            ClientId = client.ClientId,
            ClientSecret = null, // Never return client_secret on GET/PUT
            ClientIdIssuedAt = 0, // We don't track this
            ClientSecretExpiresAt = 0, // 0 = never expires
            RegistrationAccessToken = null, // Don't return on GET/PUT per RFC 7592
            RegistrationClientUri = $"{http.Request.Scheme}://{http.Request.Host}/register/{client.ClientId}",
            RedirectUris = redirectUris,
            ClientName = client.ClientName,
            SubjectType = client.SubjectType,
            SectorIdentifierUri = client.SectorIdentifierUri,
            JwksUri = client.PublicJwksUri,
            Jwks = !string.IsNullOrEmpty(client.PublicJwksJson)
                ? JsonSerializer.Deserialize<object>(client.PublicJwksJson)
                : null,
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
            PostLogoutRedirectUris = postLogoutUris
        };
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

    private IResult? CheckFeatureFlags(string method)
    {
        if (!_authOptions.EnableDynamicClientRegistration)
        {
            logger.LogWarning("{Method} /register/{{clientId}} called but dynamic client registration is disabled", method);
            return Results.Json(
                new { error = "invalid_request", error_description = "Dynamic client registration is not enabled" },
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
}
