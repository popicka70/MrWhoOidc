using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Configuration;
using Spectre.Console;

namespace MrWhoOidc.Cli.Services;

public static class CliAdminApiClient
{
    private static bool IsDryRun =>
        Environment.GetCommandLineArgs().Contains("--dry-run");

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
        string? detail = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var problem = JsonSerializer.Deserialize<ProblemPayload>(payload, JsonOptions);
                detail = !string.IsNullOrWhiteSpace(problem?.Detail)
                    ? problem.Detail
                    : problem?.Title;
            }
            catch
            {
                var snippet = payload.Length > 240 ? payload[..240] + "..." : payload;
                detail = snippet;
            }
        }

        // Map common HTTP status codes to actionable messages
        var hint = statusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " Are you logged in? Try: mrwho-cli login",
            HttpStatusCode.Forbidden =>
                " Insufficient permissions. Check your role (tenant-admin vs platform-admin). Try: mrwho-cli whoami",
            HttpStatusCode.NotFound =>
                " The resource was not found. Verify the ID and that it belongs to your current tenant.",
            HttpStatusCode.Conflict =>
                " A conflicting resource already exists. Check for duplicate names or identifiers.",
            HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest =>
                " The request was invalid. Check the provided values.",
            HttpStatusCode.TooManyRequests =>
                " Rate-limited. Wait a moment and try again, or check: mrwho-cli rate-limits overview",
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout =>
                " The server is temporarily unavailable. Check: mrwho-cli health",
            _ => ""
        };

        var baseMsg = detail ?? $"HTTP {(int)statusCode}";
        return $"{baseMsg}{hint}";
    }

    private sealed class ProblemPayload
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("detail")]
        public string? Detail { get; set; }
    }

    public static async Task<T?> GetAsync<T>(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativePath,
        CancellationToken ct = default)
    {
        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var response = await httpClient.GetAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath), ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return default;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractErrorMessage(response.StatusCode, payload));
        if (string.IsNullOrWhiteSpace(payload)) return default;
        return JsonSerializer.Deserialize<T>(payload, JsonOptions);
    }

    public static async Task<TResponse?> PostAsync<TResponse>(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativePath,
        object body,
        CancellationToken ct = default)
    {
        if (IsDryRun)
        {
            PrintDryRun("POST", connection.ServerUrl, relativePath, body);
            return default;
        }

        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.PostAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath), content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractErrorMessage(response.StatusCode, payload));
        if (string.IsNullOrWhiteSpace(payload)) return default;
        return JsonSerializer.Deserialize<TResponse>(payload, JsonOptions);
    }

    public static async Task PutAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativePath,
        object body,
        CancellationToken ct = default)
    {
        if (IsDryRun)
        {
            PrintDryRun("PUT", connection.ServerUrl, relativePath, body);
            return;
        }

        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var content = System.Net.Http.Json.JsonContent.Create(body, options: JsonOptions);
        using var response = await httpClient.PutAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath), content, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractErrorMessage(response.StatusCode, payload));
    }

    public static async Task DeleteAsync(
        CliConfig config,
        AuthenticatedConnection connection,
        string relativePath,
        CancellationToken ct = default)
    {
        if (IsDryRun)
        {
            PrintDryRun("DELETE", connection.ServerUrl, relativePath, body: null);
            return;
        }

        var accessToken = await CliServerConnection.GetValidAccessTokenAsync(config, connection).ConfigureAwait(false);
        using var httpClient = CliServerConnection.CreateAuthenticatedHttpClient(connection, accessToken);
        using var response = await httpClient.DeleteAsync(CliServerConnection.CombineRelativePath(connection.ServerUrl, relativePath), ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractErrorMessage(response.StatusCode, payload));
    }

    private static void PrintDryRun(string method, string serverUrl, string relativePath, object? body)
    {
        var url = CliServerConnection.CombineRelativePath(serverUrl, relativePath);
        AnsiConsole.MarkupLine($"[yellow]DRY RUN[/] — no changes will be made.");
        AnsiConsole.MarkupLine($"  [bold]Method:[/]  {Markup.Escape(method)}");
        AnsiConsole.MarkupLine($"  [bold]URL:[/]     {Markup.Escape(url)}");
        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
            AnsiConsole.MarkupLine($"  [bold]Body:[/]");
            AnsiConsole.WriteLine(json);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
