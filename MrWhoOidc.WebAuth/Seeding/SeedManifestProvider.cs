using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Seeding;

namespace MrWhoOidc.WebAuth.Seeding;

public interface ISeedManifestProvider
{
    Task<SeedManifest?> TryLoadAsync(CancellationToken ct = default);
}

internal sealed class SeedManifestProvider(
    IOptions<SeedManifestOptions> options,
    ILogger<SeedManifestProvider> logger) : ISeedManifestProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<SeedManifest?> TryLoadAsync(CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return null;
        }

        try
        {
            var json = TryGetJson(o);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<SeedManifest>(json, SerializerOptions);
            if (manifest is null)
            {
                logger.LogWarning("Seed manifest was present but could not be deserialized (null).");
                return null;
            }

            return manifest;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load seed manifest");
            return null;
        }
    }

    private static string? TryGetJson(SeedManifestOptions o)
    {
        if (!string.IsNullOrWhiteSpace(o.ManifestBase64))
        {
            var bytes = Convert.FromBase64String(o.ManifestBase64);
            return Encoding.UTF8.GetString(bytes);
        }

        if (!string.IsNullOrWhiteSpace(o.ManifestJson))
        {
            return o.ManifestJson;
        }

        if (!string.IsNullOrWhiteSpace(o.ManifestPath) && File.Exists(o.ManifestPath))
        {
            return File.ReadAllText(o.ManifestPath);
        }

        return null;
    }
}
