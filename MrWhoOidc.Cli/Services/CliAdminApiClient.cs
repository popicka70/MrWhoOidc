using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;

namespace MrWhoOidc.Cli.Services;

public static class CliAdminApiClient
{
    public static async Task<IReadOnlyList<T>> GetListAsync<T>(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativePath,
        CancellationToken ct = default)
    {
        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var response = await httpClient.GetAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath), ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractErrorMessage(response.StatusCode, payload));
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<T>();
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<T>>(payload, JsonOptions) ?? Array.Empty<T>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The server returned an unexpected response for {relativePath}: {ex.Message}");
        }
    }

    private static string ExtractErrorMessage(HttpStatusCode statusCode, string payload)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var problem = JsonSerializer.Deserialize<ProblemPayload>(payload, JsonOptions);
                if (!string.IsNullOrWhiteSpace(problem?.Detail))
                {
                    return problem.Detail;
                }

                if (!string.IsNullOrWhiteSpace(problem?.Title))
                {
                    return problem.Title;
                }
            }
            catch
            {
                var snippet = payload.Length > 240 ? payload[..240] + "..." : payload;
                return $"Request failed with HTTP {(int)statusCode}. Body: {snippet}";
            }
        }

        return $"Request failed with HTTP {(int)statusCode}.";
    }

    private sealed class ProblemPayload
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}