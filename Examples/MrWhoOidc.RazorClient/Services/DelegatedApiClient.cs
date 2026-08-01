using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MrWhoOidc.RazorClient.Services;

public sealed class DelegatedApiClient(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<TestApiClient.TestApiResponse> ExchangeAndCallAsync(
        string subjectToken,
        Guid delegationId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await ExchangeAsync(subjectToken, delegationId, cancellationToken)
            .ConfigureAwait(false);
        return await CallCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DelegatedDemoResponse> ExchangeAndCallProfileAsync(
        string subjectToken,
        Guid delegationId,
        CancellationToken cancellationToken = default)
    {
        var accessToken = await ExchangeAsync(subjectToken, delegationId, cancellationToken)
            .ConfigureAwait(false);
        var identity = await CallCurrentUserAsync(accessToken, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(identity.Subject, out var subjectId))
        {
            throw new InvalidOperationException("The delegated token subject is not a user account ID.");
        }

        using var profileRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiBaseAddress.TrimEnd('/')}/profiles/{subjectId}/summary");
        profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var profileResponse = await httpClient.SendAsync(profileRequest, cancellationToken)
            .ConfigureAwait(false);
        var profileJson = await profileResponse.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!profileResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Delegated profile request failed ({(int)profileResponse.StatusCode}): {profileJson}");
        }

        var profile = await profileResponse.Content
            .ReadFromJsonAsync<DelegatedProfileResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Profile API returned an empty response.");
        return new DelegatedDemoResponse(identity, profile);
    }

    private string ApiBaseAddress => configuration["TestApi:BaseAddress"]
        ?? throw new InvalidOperationException("TestApi:BaseAddress is required.");

    private async Task<string> ExchangeAsync(
        string subjectToken,
        Guid delegationId,
        CancellationToken cancellationToken)
    {
        var issuer = configuration["MrWhoOidc:Issuer"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("MrWhoOidc:Issuer is required.");
        var clientId = configuration["MrWhoOidc:ClientId"]
            ?? throw new InvalidOperationException("MrWhoOidc:ClientId is required.");
        var clientSecret = configuration["MrWhoOidc:ClientSecret"]
            ?? throw new InvalidOperationException("MrWhoOidc:ClientSecret is required.");

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, $"{issuer}/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:token-exchange",
                ["subject_token"] = subjectToken,
                ["subject_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["requested_token_type"] = "urn:ietf:params:oauth:token-type:access_token",
                ["audience"] = "api",
                ["scope"] = "profile",
                ["delegation_id"] = delegationId.ToString()
            })
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        using var tokenResponse = await httpClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Delegated token exchange failed ({(int)tokenResponse.StatusCode}): {tokenJson}");
        }

        using var document = JsonDocument.Parse(tokenJson);
        var accessToken = document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Token response did not contain access_token.");

        return accessToken;
    }

    private async Task<TestApiClient.TestApiResponse> CallCurrentUserAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiBaseAddress.TrimEnd('/')}/me");
        apiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var apiResponse = await httpClient.SendAsync(apiRequest, cancellationToken).ConfigureAwait(false);
        apiResponse.EnsureSuccessStatusCode();
        return await apiResponse.Content.ReadFromJsonAsync<TestApiClient.TestApiResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Test API returned an empty response.");
    }
}

public sealed record DelegatedDemoResponse(
    TestApiClient.TestApiResponse Identity,
    DelegatedProfileResponse Profile);

public sealed record DelegatedProfileResponse(
    string ProfileId,
    string Owner,
    string Actor,
    bool Delegated,
    string? DelegationId,
    string? ClientId,
    string Capability,
    string ResourceType,
    string ResourceId,
    string AuditReference);