using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MrWhoOidc.Auth.Entitlements.Contracts;
using MrWhoOidc.Auth.Entitlements.Options;
using MrWhoOidc.Auth.Protocols;
using MrWhoOidc.Auth.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MrWhoOidc.Auth.Entitlements;

public sealed class LicensingEntitlementsClient(
    HttpClient http,
    IOptions<LicensingIntegrationOptions> options,
    IJwtService jwt,
    ILogger<LicensingEntitlementsClient> logger) : ILicensingEntitlementsClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<EffectiveEntitlementsResponse> ResolveEffectiveEntitlementsAsync(
        EffectiveEntitlementsRequest request,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            return new EffectiveEntitlementsResponse { Entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase) };
        }

        if (string.IsNullOrWhiteSpace(opt.BaseUrl))
        {
            logger.LogWarning("LicensingIntegration enabled but BaseUrl is empty.");
            return new EffectiveEntitlementsResponse { Entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase) };
        }

        if (string.IsNullOrWhiteSpace(opt.Audience))
        {
            logger.LogWarning("LicensingIntegration enabled but Audience is empty.");
            return new EffectiveEntitlementsResponse { Entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase) };
        }

        var serviceToken = await CreateServiceTokenAsync(issuer, opt.Audience).ConfigureAwait(false);

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/entitlements/effective")
        {
            Content = JsonContent.Create(request, mediaType: new MediaTypeHeaderValue("application/json"), options: JsonOptions)
        };

        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

        using var resp = await http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            logger.LogWarning("LicensingService entitlements call failed with status {Status}", (int)resp.StatusCode);
            return new EffectiveEntitlementsResponse { Entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase) };
        }

        var model = await resp.Content.ReadFromJsonAsync<EffectiveEntitlementsResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
        return model ?? new EffectiveEntitlementsResponse { Entitlements = new Dictionary<string, Entitlement>(StringComparer.OrdinalIgnoreCase) };
    }

    public async Task<SignedLicenseTokenResult> GetSignedLicenseTokenAsync(
        SignedLicenseTokenRequest request,
        string issuer,
        CancellationToken cancellationToken = default)
    {
        var opt = options.Value;
        if (!opt.Enabled)
        {
            logger.LogDebug("LicensingIntegration disabled, skipping signed license token request");
            return SignedLicenseTokenResult.Fail("disabled", "Licensing integration is disabled");
        }

        if (string.IsNullOrWhiteSpace(opt.BaseUrl))
        {
            logger.LogWarning("LicensingIntegration enabled but BaseUrl is empty.");
            return SignedLicenseTokenResult.Fail("configuration_error", "LicensingService BaseUrl is not configured");
        }

        if (string.IsNullOrWhiteSpace(opt.Audience))
        {
            logger.LogWarning("LicensingIntegration enabled but Audience is empty.");
            return SignedLicenseTokenResult.Fail("configuration_error", "LicensingService Audience is not configured");
        }

        try
        {
            var serviceToken = await CreateServiceTokenAsync(issuer, opt.Audience, "licensing.signed-tokens").ConfigureAwait(false);

            using var msg = new HttpRequestMessage(HttpMethod.Post, "/api/licenses/signed-token")
            {
                Content = JsonContent.Create(request, mediaType: new MediaTypeHeaderValue("application/json"), options: JsonOptions)
            };

            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceToken);

            using var resp = await http.SendAsync(msg, cancellationToken).ConfigureAwait(false);
            
            if (resp.IsSuccessStatusCode)
            {
                var tokenResponse = await resp.Content.ReadFromJsonAsync<SignedLicenseTokenResponse>(JsonOptions, cancellationToken).ConfigureAwait(false);
                if (tokenResponse is not null)
                {
                    logger.LogDebug(
                        "Successfully retrieved signed license token for subject {SubjectId}, product {ProductKey}",
                        request.SubjectId, request.ProductKey);
                    return SignedLicenseTokenResult.Ok(tokenResponse);
                }

                return SignedLicenseTokenResult.Fail("invalid_response", "LicensingService returned empty response");
            }

            // Try to parse error response
            try
            {
                var errorResponse = await resp.Content.ReadFromJsonAsync<SignedLicenseTokenError>(JsonOptions, cancellationToken).ConfigureAwait(false);
                if (errorResponse is not null)
                {
                    logger.LogWarning(
                        "LicensingService signed token request failed: {Error} - {Description}",
                        errorResponse.Error, errorResponse.ErrorDescription);
                    return SignedLicenseTokenResult.Fail(errorResponse);
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parse errors and fall through to generic error
            }

            logger.LogWarning(
                "LicensingService signed token request failed with status {Status}",
                (int)resp.StatusCode);
            return SignedLicenseTokenResult.Fail("service_error", $"LicensingService returned status {(int)resp.StatusCode}");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "HTTP error calling LicensingService signed token endpoint");
            return SignedLicenseTokenResult.Fail("network_error", "Failed to connect to LicensingService");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Timeout calling LicensingService signed token endpoint");
            return SignedLicenseTokenResult.Fail("timeout", "LicensingService request timed out");
        }
    }

    private async Task<string> CreateServiceTokenAsync(string issuer, string audience, string scope = "licensing.entitlements")
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", "mrwhooidc"),
            new("client_id", "mrwhooidc"),
            new("scope", scope)
        };

        return await jwt.CreateJwtAsync(issuer, audience, claims, DateTimeOffset.UtcNow.AddMinutes(1), tokenType: SecurityConstants.JwtTokenTypes.AtJwt).ConfigureAwait(false);
    }
}
