using System.Net.Http.Headers;
using System.Text.Json;

namespace MrWhoOidc.TestApi.Services;

public sealed class DelegatedTokenIntrospectionService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<DelegatedTokenIntrospectionService> logger)
{
    public async Task<DelegatedTokenIntrospection?> IntrospectAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        var issuer = configuration["MrWhoOidc:Issuer"]?.TrimEnd('/');
        var clientId = configuration["MrWhoOidc:ClientId"];
        var clientSecret = configuration["MrWhoOidc:ClientSecret"];
        if (string.IsNullOrWhiteSpace(issuer)
            || string.IsNullOrWhiteSpace(clientId)
            || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException(
                "MrWhoOidc:Issuer, MrWhoOidc:ClientId, and MrWhoOidc:ClientSecret are required.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{issuer}/introspect")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["token"] = accessToken,
                ["token_type_hint"] = "access_token"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Token introspection failed with status code {StatusCode}", response.StatusCode);
            return null;
        }

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var root = document.RootElement;
        if (!root.TryGetProperty("active", out var active)
            || active.ValueKind != JsonValueKind.True)
        {
            return null;
        }

        var scopes = root.TryGetProperty("scope", out var scope)
            && scope.ValueKind == JsonValueKind.String
            ? scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();
        var subject = GetString(root, "sub");
        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var actor = ParseActor(root.TryGetProperty("act", out var act) ? act : default);
        var resources = root.TryGetProperty("delegated_resources", out var delegatedResources)
            ? delegatedResources.Clone()
            : (JsonElement?)null;

        return new DelegatedTokenIntrospection(
            Subject: subject,
            Actor: actor,
            ClientId: GetString(root, "client_id") ?? GetString(root, "azp"),
            DelegationId: GetString(root, "delegation_id"),
            Audience: ReadAudience(root),
            Scopes: scopes,
            DelegatedResources: resources);
    }

    private static string? GetString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ParseActor(JsonElement act)
    {
        if (act.ValueKind != JsonValueKind.Object
            || !act.TryGetProperty("sub", out var subject)
            || subject.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return subject.GetString();
    }

    private static IReadOnlySet<string> ReadAudience(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var audience))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var values = audience.ValueKind switch
        {
            JsonValueKind.String => [audience.GetString()!],
            JsonValueKind.Array => audience.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String)
                .Select(value => value.GetString()!)
                .ToArray(),
            _ => Array.Empty<string>()
        };

        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}

public sealed record DelegatedTokenIntrospection(
    string Subject,
    string? Actor,
    string? ClientId,
    string? DelegationId,
    IReadOnlySet<string> Audience,
    IReadOnlyList<string> Scopes,
    JsonElement? DelegatedResources)
{
    public bool AllowsResource(string capability, string resourceType, string resourceId)
    {
        if (DelegatedResources is not { } resources
            || resources.ValueKind != JsonValueKind.Object
            || !resources.TryGetProperty(capability, out var policy)
            || policy.ValueKind != JsonValueKind.Object
            || !policy.TryGetProperty("allowedTypes", out var allowedTypes)
            || !policy.TryGetProperty("allowedIds", out var allowedIds)
            || allowedTypes.ValueKind != JsonValueKind.Array
            || allowedIds.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var typeAllowed = allowedTypes.EnumerateArray()
            .Any(value => value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), resourceType, StringComparison.OrdinalIgnoreCase));
        var idAllowed = allowedIds.EnumerateArray()
            .Any(value => value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString(), resourceId, StringComparison.Ordinal));
        return typeAllowed && idAllowed;
    }
}
