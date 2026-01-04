using System.Text.Json;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Fido2NetLib;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Handlers;

public interface IWebAuthnHandler
{
    Task<IResult> RegistrationChallengeAsync(HttpContext context);
    Task<IResult> RegistrationCompletionAsync(HttpContext context);
    Task<IResult> AuthenticationChallengeAsync(HttpContext context);
    Task<IResult> AuthenticationCompletionAsync(HttpContext context);
    Task<IResult> GetUserCredentialsAsync(HttpContext context);
}

public sealed class WebAuthnHandler(
    IWebAuthnService webAuthnService,
    ITenantAccessor tenantAccessor,
    AuthDbContext dbContext,
    ILogger<WebAuthnHandler> logger,
    IMultiTenancyOptions multiTenancyOptions,
    ITenantSettingsService settingsService) : IWebAuthnHandler
{
    public async Task<IResult> RegistrationChallengeAsync(HttpContext context)
    {
        try
        {
            var userId = GetAuthenticatedUserId(context);
            if (userId == null)
            {
                return Results.Unauthorized();
            }

            var tenantContext = tenantAccessor.CurrentTenant;
            if (tenantContext == null)
            {
                return Results.BadRequest("Tenant context required");
            }

            var user = await dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == userId.Value && u.TenantId == tenantContext.TenantId, 
                    context.RequestAborted);
            
            if (user == null)
            {
                return Results.NotFound("User not found");
            }

            var (options, sessionId) = await webAuthnService.CreateRegistrationChallengeAsync(
                user, true, context.RequestAborted);

            return Results.Json(new { options, sessionId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating registration challenge");
            return Results.Problem("Failed to create registration challenge");
        }
    }

    public async Task<IResult> RegistrationCompletionAsync(HttpContext context)
    {
        try
        {
            var userId = GetAuthenticatedUserId(context);
            if (userId == null)
            {
                return Results.Unauthorized();
            }

            var tenantContext = tenantAccessor.CurrentTenant;
            if (tenantContext == null)
            {
                return Results.BadRequest("Tenant context required");
            }

            var requestBody = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            if (requestBody.ValueKind == JsonValueKind.Undefined)
            {
                return Results.BadRequest("Invalid request body");
            }

            var user = await dbContext.Users
                .FirstOrDefaultAsync(u => u.Id == userId.Value && u.TenantId == tenantContext.TenantId, 
                    context.RequestAborted);
            
            if (user == null)
            {
                return Results.NotFound("User not found");
            }

            // Extract sessionId and attestationResponse from request
            var sessionId = requestBody.GetProperty("sessionId").GetString();
            var friendlyName = requestBody.TryGetProperty("friendlyName", out var nameElement) 
                ? nameElement.GetString() 
                : "WebAuthn Key";

            // Extract the attestation response
            var attestationElement = requestBody.GetProperty("attestationResponse");
            var attestationResponse = JsonSerializer.Deserialize<AuthenticatorAttestationRawResponse>(attestationElement);

            if (attestationResponse == null)
            {
                return Results.BadRequest("Invalid attestation response");
            }

            var (success, credentialId, errorMessage) = await webAuthnService.CompleteRegistrationAsync(
                user, attestationResponse, sessionId!, friendlyName, context.RequestAborted);

            if (success)
            {
                return Results.Json(new { success = true, credentialId });
            }

            return Results.BadRequest(errorMessage ?? "Registration failed");
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid registration completion request");
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error completing registration");
            return Results.Problem("Failed to complete registration");
        }
    }

    public async Task<IResult> AuthenticationChallengeAsync(HttpContext context)
    {
        try
        {
            var tenantContext = tenantAccessor.CurrentTenant;
            if (tenantContext == null)
            {
                return Results.BadRequest("Tenant context required");
            }

            // Read optional username from query parameters for usernameless flows
            var username = context.Request.Query["username"].ToString();

            var (options, sessionId) = await webAuthnService.CreateAuthenticationChallengeAsync(
                username, context.RequestAborted);

            return Results.Json(new { options, sessionId });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating authentication challenge");
            return Results.Problem("Failed to create authentication challenge");
        }
    }

    public async Task<IResult> AuthenticationCompletionAsync(HttpContext context)
    {
        try
        {
            var tenantContext = tenantAccessor.CurrentTenant;
            if (tenantContext == null)
            {
                return Results.BadRequest("Tenant context required");
            }

            var requestBody = await context.Request.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
            if (requestBody.ValueKind == JsonValueKind.Undefined)
            {
                return Results.BadRequest("Invalid request body");
            }

            // Extract sessionId from request
            var sessionId = requestBody.GetProperty("sessionId").GetString();
            
            // Extract optional return URL from request
            var returnUrl = requestBody.TryGetProperty("returnUrl", out var returnUrlElement) 
                ? returnUrlElement.GetString() 
                : null;
            
            // Extract the assertion response
            var assertionElement = requestBody.GetProperty("assertionResponse");
            var assertionResponse = JsonSerializer.Deserialize<AuthenticatorAssertionRawResponse>(assertionElement);

            if (assertionResponse == null)
            {
                return Results.BadRequest("Invalid assertion response");
            }

            var (success, user, errorMessage) = await webAuthnService.CompleteAuthenticationAsync(
                assertionResponse, sessionId!, context.RequestAborted);

            if (!success || user == null)
            {
                return Results.BadRequest(errorMessage ?? "Authentication failed");
            }

            logger.LogInformation("✅ [WebAuthn] User {Username} authenticated successfully with WebAuthn", user.Username);

            // Check tenant MFA requirement (similar to password login)
            var settings = await settingsService.GetCurrentTenantSettingsAsync();
            var mfaRequired = settings.Auth?.RequireMfa ?? false;

            // If MFA is required but user doesn't have it enabled, redirect to enrollment
            if (mfaRequired && !user.TotpEnabled)
            {
                // Issue short-lived preauth to allow MFA enrollment
                var preauthClaims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new(ClaimTypes.Name, user.Username),
                    new("amr", "webauthn"),
                    new("mfa_enrollment_required", "true")
                };
                var preauthIdentity = new ClaimsIdentity(preauthClaims, "preauth");
                await context.SignInAsync("preauth", new ClaimsPrincipal(preauthIdentity));

                logger.LogInformation("⚠️ [WebAuthn] User {User} requires MFA enrollment (tenant policy). Redirecting to /Mfa", user.Username);
                
                return Results.Json(new 
                { 
                    success = true,
                    requiresMfaEnrollment = true,
                    redirectUrl = "/mfa?required=true"
                });
            }

            // If TOTP enabled, issue short-lived preauth and redirect to TOTP page
            if (user.TotpEnabled)
            {
                var claims = new List<Claim>
                {
                    new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                    new(ClaimTypes.Name, user.Username),
                    new("amr", "webauthn")
                };
                var identity = new ClaimsIdentity(claims, "preauth");
                await context.SignInAsync("preauth", new ClaimsPrincipal(identity));
                
                logger.LogInformation("🔐 [WebAuthn] User {User} requires TOTP verification", user.Username);
                
                return Results.Json(new 
                { 
                    success = true,
                    requiresTotp = true,
                    redirectUrl = "/LoginTotp"
                });
            }

            // Complete authentication - sign user in
            var finalClaims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new(ClaimTypes.Name, user.Username),
                new(OidcConstants.Claims.AuthTime, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString()),
                new(OidcConstants.Claims.Amr, "webauthn"),
                new(OidcConstants.Claims.Acr, OidcConstants.AcrValues.Passkey),
                new(OidcConstants.Claims.Idp, "local")
            };

            var finalIdentity = new ClaimsIdentity(finalClaims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(finalIdentity);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            logger.LogInformation("✅ [WebAuthn] User {User} signed in successfully", user.Username);

            // Build redirect URL based on return URL or default
            string redirectUrl;
            
            if (!string.IsNullOrEmpty(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
            {
                redirectUrl = returnUrl;
                logger.LogInformation("➡️ [WebAuthn] Redirecting to provided ReturnUrl: {ReturnUrl}", returnUrl);
            }
            else
            {
                // Build default redirect URL based on tenant mode (similar to password login)
                var currentTenant = tenantAccessor.CurrentTenant;

                if (multiTenancyOptions.Enabled && currentTenant != null)
                {
                    // Multi-tenant mode: redirect to /t/{slug}/
                    redirectUrl = $"/t/{currentTenant.Slug}/";
                    logger.LogInformation("➡️ [WebAuthn] Multi-tenant mode: redirecting to {DefaultUrl} (Tenant: {TenantSlug})",
                        redirectUrl, currentTenant.Slug);
                }
                else
                {
                    // Single-tenant mode: redirect to root /
                    redirectUrl = "/";
                    logger.LogInformation("➡️ [WebAuthn] Single-tenant mode: redirecting to {DefaultUrl}", redirectUrl);
                }
            }

            return Results.Json(new 
            { 
                success = true,
                userId = user.Id,
                username = user.Username,
                redirectUrl = redirectUrl
            });
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid authentication completion request");
            return Results.BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error completing authentication");
            return Results.Problem("Failed to complete authentication");
        }
    }

    public async Task<IResult> GetUserCredentialsAsync(HttpContext context)
    {
        try
        {
            var userId = GetAuthenticatedUserId(context);
            if (userId == null)
            {
                return Results.Unauthorized();
            }

            var tenantContext = tenantAccessor.CurrentTenant;
            if (tenantContext == null)
            {
                return Results.BadRequest("Tenant context required");
            }

            var credentials = await webAuthnService.GetUserCredentialsAsync(
                userId.Value, context.RequestAborted);

            var result = credentials.Select(c => new
            {
                id = c.Id,
                friendlyName = c.FriendlyName,
                deviceType = c.DeviceType,
                createdAt = c.CreatedAt,
                lastUsedAt = c.LastUsedAt,
                isActive = c.IsActive
            });

            return Results.Json(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error retrieving user credentials");
            return Results.Problem("Failed to retrieve credentials");
        }
    }

    private static Guid? GetAuthenticatedUserId(HttpContext context)
    {
        var userIdClaim = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}