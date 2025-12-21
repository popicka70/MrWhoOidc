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

        var serviceToken = CreateServiceToken(issuer, opt.Audience);

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

    private string CreateServiceToken(string issuer, string audience)
    {
        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", "mrwhooidc"),
            new("client_id", "mrwhooidc"),
            new("scope", "licensing.entitlements")
        };

        return jwt.CreateJwt(issuer, audience, claims, DateTimeOffset.UtcNow.AddMinutes(1), tokenType: SecurityConstants.JwtTokenTypes.AtJwt);
    }
}
