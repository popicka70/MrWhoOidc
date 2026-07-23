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
        var issuer = configuration["MrWhoOidc:Issuer"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("MrWhoOidc:Issuer is required.");
        var clientId = configuration["MrWhoOidc:ClientId"]
            ?? throw new InvalidOperationException("MrWhoOidc:ClientId is required.");
        var clientSecret = configuration["MrWhoOidc:ClientSecret"]
            ?? throw new InvalidOperationException("MrWhoOidc:ClientSecret is required.");
        var apiBase = configuration["TestApi:BaseAddress"]
            ?? throw new InvalidOperationException("TestApi:BaseAddress is required.");

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

        using var apiRequest = new HttpRequestMessage(HttpMethod.Get, $"{apiBase.TrimEnd('/')}/me");
        apiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var apiResponse = await httpClient.SendAsync(apiRequest, cancellationToken).ConfigureAwait(false);
        apiResponse.EnsureSuccessStatusCode();
        return await apiResponse.Content.ReadFromJsonAsync<TestApiClient.TestApiResponse>(cancellationToken: cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Test API returned an empty response.");
    }
}