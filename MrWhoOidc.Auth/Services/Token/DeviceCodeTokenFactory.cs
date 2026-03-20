using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MrWhoOidc.Auth.Services.Token;

/// <summary>
/// Implementation of IDeviceCodeTokenFactory for RFC 8628 device authorization grant.
/// Issues access tokens and refresh tokens for users who authorized a device.
/// </summary>
public sealed class DeviceCodeTokenFactory(
    AuthDbContext db,
    IJwtService jwt,
    ITenantSettingsService settingsService,
    IScopeResolver scopeResolver,
    ITokenLifetimeResolver lifetimeResolver) : IDeviceCodeTokenFactory
{
    public async Task<(bool ok, object? payload, string? error, int status)> CreateTokenAsync(DeviceCodeTokenRequest request, CancellationToken ct = default)
    {
        var settings = await settingsService.GetCurrentTenantSettingsAsync().ConfigureAwait(false);

        // Get client
        var client = await db.Clients.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ClientId == request.ClientId, ct)
            .ConfigureAwait(false);

        if (client is null)
        {
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidClient }, OAuthConstants.ErrorCodes.InvalidClient, 400);
        }

        // Get user
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == request.UserId, ct)
            .ConfigureAwait(false);

        if (user is null)
        {
            return (false, new { error = OAuthConstants.ErrorCodes.InvalidGrant, error_description = "User not found" }, OAuthConstants.ErrorCodes.InvalidGrant, 400);
        }

        // Filter scopes to allowed ones
        var allowedScopeNames = await db.ClientScopes.AsNoTracking()
            .Where(cs => cs.ClientId == client.Id)
            .Select(cs => cs.ScopeName)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var granted = new List<string>();
        var includeRefreshToken = false;

        foreach (var scope in request.Scopes)
        {
            if (string.IsNullOrWhiteSpace(scope)) continue;
            
            if (string.Equals(scope, OidcConstants.Scopes.OfflineAccess, StringComparison.Ordinal))
            {
                includeRefreshToken = true;
                granted.Add(scope);
                continue;
            }

            // Allow openid and standard OIDC scopes
            if (string.Equals(scope, OidcConstants.Scopes.OpenId, StringComparison.Ordinal) ||
                OidcConstants.Scopes.AllStandardScopes.Contains(scope))
            {
                granted.Add(scope);
                continue;
            }

            // Check client-specific scope allowance
            if (allowedScopeNames.Contains(scope, StringComparer.Ordinal))
            {
                granted.Add(scope);
            }
        }

        // Build access token claims
        var jti = Guid.NewGuid().ToString("N");
        var claims = new List<Claim>
        {
            new(OidcConstants.Claims.Subject, user.Id.ToString()),
            new("client_id", request.ClientId),
            new("jti", jti)
        };

        // Add scope claim
        if (granted.Count > 0)
        {
            claims.Add(new("scope", string.Join(' ', granted)));
        }

        // Add user profile claims if profile scope requested
        if (granted.Contains(OidcConstants.Scopes.Profile, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(user.Name))
            {
                claims.Add(new(OidcConstants.Claims.Name, user.Name));
            }
        }

        // Add email claim if email scope requested
        if (granted.Contains(OidcConstants.Scopes.Email, StringComparer.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(user.Email))
            {
                claims.Add(new(OidcConstants.Claims.Email, user.Email));
                claims.Add(new(OidcConstants.Claims.EmailVerified, user.EmailVerified.ToString().ToLowerInvariant()));
            }
        }

        // Add DPoP binding if present
        if (!string.IsNullOrEmpty(request.DpopJkt))
        {
            var cnf = JsonSerializer.Serialize(new { jkt = request.DpopJkt });
            claims.Add(new("cnf", cnf));
        }

        // Add realm if available
        var realmName = await db.Realms.AsNoTracking()
            .Where(r => r.Id == client.RealmId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrEmpty(realmName))
        {
            claims.Add(new("realm", realmName));
        }

        // Add roles if the "roles" scope was granted.
        // Query across ALL realms in the tenant so that cross-realm roles
        // (e.g., platform-admin in the platform realm) are included even when
        // the CLI client belongs to the default realm.
        if (granted.Contains(OidcConstants.Scopes.Roles, StringComparer.OrdinalIgnoreCase) && client.TenantId != Guid.Empty)
        {
            var roleNames = await (
                from assignment in db.UserRealmRoleAssignments.AsNoTracking()
                where assignment.UserId == user.Id && assignment.IsActive
                join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                join realm in db.Realms.AsNoTracking() on assignment.RealmId equals realm.Id
                where role.IsActive && realm.TenantId == client.TenantId
                select role.Name
            ).Union(
                from assignment in db.UserClientRoleAssignments.AsNoTracking()
                where assignment.UserId == user.Id && assignment.ClientId == client.Id && assignment.IsActive
                join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                where role.IsActive
                select role.Name
            ).Distinct().ToArrayAsync(ct).ConfigureAwait(false);

            foreach (var roleName in roleNames)
            {
                claims.Add(new(OidcConstants.Claims.Roles, roleName));
            }
        }

        // Add tenant_id if relevant
        var hasCustomScopes = granted.Any(s => !scopeResolver.IsStandardScope(s));
        if (hasCustomScopes && client.TenantId != Guid.Empty)
        {
            claims.Add(new("tenant_id", client.TenantId.ToString()));
        }

        // Determine token lifetime
        var accessTokenLifetime = lifetimeResolver.ResolveAccessTokenLifetime(client, settings);
        var accessTokenExpiry = DateTimeOffset.UtcNow.Add(accessTokenLifetime);

        // Create access token
        var tokenType = string.IsNullOrEmpty(request.DpopJkt) ? "Bearer" : "DPoP";
        var accessToken = await jwt.CreateJwtAsync(
            request.Issuer,
            request.Audience,
            claims,
            accessTokenExpiry,
            tokenType: SecurityConstants.JwtTokenTypes.AtJwt,
            ct: ct).ConfigureAwait(false);

        // Build response
        var response = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["token_type"] = tokenType,
            ["expires_in"] = (int)accessTokenLifetime.TotalSeconds
        };

        if (granted.Count > 0)
        {
            response["scope"] = string.Join(' ', granted);
        }

        // Create refresh token if offline_access was requested
        if (includeRefreshToken)
        {
            var refreshTokenLifetime = lifetimeResolver.ResolveRefreshTokenLifetime(client, settings);
            var refreshTokenExpiry = DateTimeOffset.UtcNow.Add(refreshTokenLifetime);

            var refreshToken = GenerateRefreshToken();
            var refreshTokenHash = ComputeTokenHash(refreshToken);

            // Store refresh token in database
            var tokenRecord = new Persistence.Token
            {
                TenantId = request.TenantId ?? client.TenantId,
                Type = "refresh",
                TokenHash = refreshTokenHash,
                UserId = user.Id,
                ClientId = request.ClientId,
                ScopesJson = JsonSerializer.Serialize(granted),
                Audience = request.Audience,
                Jti = jti,
                CnfJkt = request.DpopJkt,
                ExpiresAt = refreshTokenExpiry,
                IpAddress = request.IpAddress,
                UserAgent = request.UserAgent
            };

            db.Tokens.Add(tokenRecord);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            response["refresh_token"] = refreshToken;
        }

        return (true, response, null, 200);
    }

    private static string GenerateRefreshToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');
    }

    private static string ComputeTokenHash(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
