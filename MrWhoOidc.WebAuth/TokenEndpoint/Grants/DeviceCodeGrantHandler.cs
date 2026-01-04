using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Utils;
using MrWhoOidc.WebAuth.Extensions;
using MrWhoOidc.WebAuth.Handlers;
using System.Text.Json;

namespace MrWhoOidc.WebAuth.TokenEndpoint.Grants;

/// <summary>
/// Handles the urn:ietf:params:oauth:grant-type:device_code grant (RFC 8628).
/// Validates the device_code and issues tokens if the user has authorized the request.
/// </summary>
public sealed class DeviceCodeGrantHandler(
    AuthDbContext db,
    ITokenService tokens,
    IOptions<AuthOptions> authOptions,
    ILogger<DeviceCodeGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => OAuthConstants.GrantTypes.DeviceCode;

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var options = authOptions.Value;

        // RFC 8628: device_code is required
        var deviceCode = context.Form[OAuthConstants.Parameters.DeviceCode].ToString();
        if (string.IsNullOrWhiteSpace(deviceCode))
        {
            logger.LogWarning("[DeviceCode] Missing device_code for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidRequest, "Missing device_code"));
        }

        // Find the device code entry
        var entry = await db.DeviceCodes
            .FirstOrDefaultAsync(dc => dc.DeviceCode == deviceCode && dc.TenantId == context.TenantId, context.Http.RequestAborted);

        if (entry == null)
        {
            logger.LogWarning("[DeviceCode] Unknown device_code for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidGrant, "Unknown device_code"));
        }

        // Verify client_id matches
        if (!string.Equals(entry.ClientId, context.ClientId, StringComparison.Ordinal))
        {
            logger.LogWarning("[DeviceCode] client_id mismatch: expected {Expected}, got {Actual}",
                Bucketization.Bucket(entry.ClientId), Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidGrant, "client_id mismatch"));
        }

        // Check expiration
        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            // Mark as expired if not already
            if (entry.Status == DeviceCodeStatus.Pending)
            {
                entry.Status = DeviceCodeStatus.Expired;
                await db.SaveChangesAsync(context.Http.RequestAborted);
            }
            logger.LogInformation("[DeviceCode] Expired device_code for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.ExpiredToken, "device_code has expired"));
        }

        // Slow-down enforcement (RFC 8628 §3.5)
        if (entry.LastPolledAt.HasValue)
        {
            var timeSinceLastPoll = DateTimeOffset.UtcNow - entry.LastPolledAt.Value;
            if (timeSinceLastPoll.TotalSeconds < entry.IntervalSeconds)
            {
                // Increase interval by 5 seconds per RFC 8628 recommendation
                entry.IntervalSeconds += 5;
                entry.LastPolledAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(context.Http.RequestAborted);

                logger.LogDebug("[DeviceCode] Slow down: client {ClientHash} polling too fast", Bucketization.Bucket(context.ClientId));
                return new GrantExecutionResult(true, false, ErrorResultWithInterval(OAuthConstants.ErrorCodes.SlowDown,
                    "Polling too frequently", entry.IntervalSeconds));
            }
        }

        // Update last polled time
        entry.LastPolledAt = DateTimeOffset.UtcNow;

        // Check status
        switch (entry.Status)
        {
            case DeviceCodeStatus.Pending:
                await db.SaveChangesAsync(context.Http.RequestAborted);
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.AuthorizationPending, "User has not yet authorized this device"));

            case DeviceCodeStatus.Denied:
                // Clean up
                db.DeviceCodes.Remove(entry);
                await db.SaveChangesAsync(context.Http.RequestAborted);
                logger.LogInformation("[DeviceCode] User denied authorization for client {ClientHash}", Bucketization.Bucket(context.ClientId));
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.AccessDenied, "User denied authorization"));

            case DeviceCodeStatus.Expired:
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.ExpiredToken, "device_code has expired"));

            case DeviceCodeStatus.Authorized:
                // Proceed to issue tokens
                break;

            default:
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.ServerError, "Unknown device code status"));
        }

        // User has authorized - issue tokens
        if (!entry.UserId.HasValue)
        {
            logger.LogError("[DeviceCode] Authorized entry missing UserId for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false,
                ErrorResult(OAuthConstants.ErrorCodes.ServerError, "Missing user information"));
        }

        var scopes = JsonSerializer.Deserialize<string[]>(entry.ScopesJson) ?? Array.Empty<string>();
        var audience = entry.Resource ?? "api";
        var issuer = context.Http.GetIssuer(context.Options);

        // Issue tokens using the existing token service
        var ipAddress = context.Http.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Http.Request.Headers.UserAgent.ToString();

        var (ok, payload, _, status) = await tokens.CreateDeviceCodeTokenAsync(
            context.ClientId,
            entry.UserId.Value,
            scopes,
            audience,
            issuer,
            context.DPoPJkt,
            ipAddress,
            userAgent,
            context.TenantId);

        if (ok)
        {
            // Remove the device code entry after successful token issuance
            db.DeviceCodes.Remove(entry);
            await db.SaveChangesAsync(context.Http.RequestAborted);
            logger.LogInformation("[DeviceCode] Tokens issued for client {ClientHash} user {UserHash}",
                Bucketization.Bucket(context.ClientId), Bucketization.Bucket(entry.UserId.Value.ToString()));
        }
        else
        {
            logger.LogWarning("[DeviceCode] Token issuance failed for client {ClientHash}", Bucketization.Bucket(context.ClientId));
        }

        var result = Results.Json(payload!, statusCode: status);
        return new GrantExecutionResult(true, ok, result);
    }

    private static IResult ErrorResult(string error, string? description)
    {
        var body = new Dictionary<string, object>
        {
            ["error"] = error
        };
        if (!string.IsNullOrEmpty(description))
        {
            body["error_description"] = description;
        }
        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest);
    }

    private static IResult ErrorResultWithInterval(string error, string? description, int interval)
    {
        var body = new Dictionary<string, object>
        {
            ["error"] = error,
            ["interval"] = interval
        };
        if (!string.IsNullOrEmpty(description))
        {
            body["error_description"] = description;
        }
        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest);
    }
}
