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
/// Handles the urn:openid:params:grant-type:ciba grant (OpenID Connect CIBA Core 1.0).
/// Validates the auth_req_id and issues tokens if the user has authorized the request (poll mode).
/// </summary>
public sealed class CibaGrantHandler(
    AuthDbContext db,
    ITokenService tokens,
    IOptions<AuthOptions> authOptions,
    ICibaNotificationService notificationService,
    ILogger<CibaGrantHandler> logger) : ITokenGrantHandler
{
    public string GrantType => OAuthConstants.GrantTypes.Ciba;

    public async Task<GrantExecutionResult> TryHandleAsync(TokenRequestContext context)
    {
        if (!string.Equals(context.GrantType, GrantType, StringComparison.Ordinal))
            return new GrantExecutionResult(false, false, null);

        var options = authOptions.Value;

        // Feature check
        if (!options.EnableCiba)
        {
            return new GrantExecutionResult(true, false,
                ErrorResult(OAuthConstants.ErrorCodes.UnsupportedGrantType, "CIBA is not enabled"));
        }

        // CIBA: auth_req_id is required
        var authReqId = context.Form[OAuthConstants.Parameters.AuthReqId].ToString();
        if (string.IsNullOrWhiteSpace(authReqId))
        {
            logger.LogWarning("[CIBA] Missing auth_req_id for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidRequest, "Missing auth_req_id"));
        }

        // Find the CIBA request entry
        var entry = await db.CibaAuthenticationRequests
            .FirstOrDefaultAsync(r => r.AuthReqId == authReqId && r.TenantId == context.TenantId, context.Http.RequestAborted);

        if (entry == null)
        {
            logger.LogWarning("[CIBA] Unknown auth_req_id for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidGrant, "Unknown auth_req_id"));
        }

        // Verify client_id matches
        if (!string.Equals(entry.ClientId, context.ClientId, StringComparison.Ordinal))
        {
            logger.LogWarning("[CIBA] client_id mismatch: expected {Expected}, got {Actual}",
                Bucketization.Bucket(entry.ClientId), Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.InvalidGrant, "client_id mismatch"));
        }

        // Check expiration
        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            // Mark as expired if not already
            if (entry.Status == CibaRequestStatus.Pending)
            {
                entry.Status = CibaRequestStatus.Expired;
                await db.SaveChangesAsync(context.Http.RequestAborted);
            }
            logger.LogInformation("[CIBA] Expired auth_req_id for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false, ErrorResult(OAuthConstants.ErrorCodes.ExpiredToken, "auth_req_id has expired"));
        }

        // Slow-down enforcement (CIBA poll mode §10.1)
        if (entry.LastPolledAt.HasValue)
        {
            var timeSinceLastPoll = DateTimeOffset.UtcNow - entry.LastPolledAt.Value;
            if (timeSinceLastPoll.TotalSeconds < entry.IntervalSeconds)
            {
                // Increase interval by 5 seconds per spec recommendation
                entry.IntervalSeconds += 5;
                entry.LastPolledAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(context.Http.RequestAborted);

                logger.LogDebug("[CIBA] Slow down: client {ClientHash} polling too fast", Bucketization.Bucket(context.ClientId));
                return new GrantExecutionResult(true, false, ErrorResultWithInterval(OAuthConstants.ErrorCodes.SlowDown,
                    "Polling too frequently", entry.IntervalSeconds));
            }
        }

        // Update last polled time
        entry.LastPolledAt = DateTimeOffset.UtcNow;

        // Check status
        switch (entry.Status)
        {
            case CibaRequestStatus.Pending:
                await db.SaveChangesAsync(context.Http.RequestAborted);
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.AuthorizationPending, "User has not yet authorized this request"));

            case CibaRequestStatus.Denied:
                // Clean up
                db.CibaAuthenticationRequests.Remove(entry);
                await db.SaveChangesAsync(context.Http.RequestAborted);
                logger.LogInformation("[CIBA] User denied authorization for client {ClientHash}", Bucketization.Bucket(context.ClientId));
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.AccessDenied, "User denied authorization"));

            case CibaRequestStatus.Expired:
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.ExpiredToken, "auth_req_id has expired"));

            case CibaRequestStatus.Authorized:
                // Proceed to issue tokens
                break;

            default:
                return new GrantExecutionResult(true, false,
                    ErrorResult(OAuthConstants.ErrorCodes.ServerError, "Unknown CIBA request status"));
        }

        // User has authorized - issue tokens
        if (!entry.UserId.HasValue)
        {
            logger.LogError("[CIBA] Authorized entry missing UserId for client {ClientHash}", Bucketization.Bucket(context.ClientId));
            return new GrantExecutionResult(true, false,
                ErrorResult(OAuthConstants.ErrorCodes.ServerError, "Missing user information"));
        }

        var scopes = JsonSerializer.Deserialize<string[]>(entry.ScopesJson) ?? Array.Empty<string>();
        var audience = entry.Resource ?? "api";
        var issuer = context.Http.GetIssuer(context.Options);

        // Issue tokens using the existing token service
        var ipAddress = context.Http.Connection.RemoteIpAddress?.ToString();
        var userAgent = context.Http.Request.Headers.UserAgent.ToString();

        // Use the same token issuance as device code flow
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
            // Remove the CIBA request entry after successful token issuance
            db.CibaAuthenticationRequests.Remove(entry);
            await db.SaveChangesAsync(context.Http.RequestAborted);
            logger.LogInformation("[CIBA] Tokens issued for client {ClientHash} user {UserHash}",
                Bucketization.Bucket(context.ClientId), Bucketization.Bucket(entry.UserId.Value.ToString()));
        }
        else
        {
            logger.LogWarning("[CIBA] Token issuance failed for client {ClientHash}", Bucketization.Bucket(context.ClientId));
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
