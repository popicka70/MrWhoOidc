using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.RazorClient.Services;

public sealed class TestApiClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<TestApiClient> _logger;

    public TestApiClient(HttpClient httpClient, ILogger<TestApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TestApiResponse?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("me", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Test API call failed with status code {StatusCode}", response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadFromJsonAsync<TestApiResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return payload;
    }

    public sealed record TestApiResponse(
        string? Subject,
        string? Name,
        string? Email,
        string? Audience,
        string? ActorClient,
        IEnumerable<string>? Scopes,
        string? IssuedAt,
        string? ExpiresAt);
}
