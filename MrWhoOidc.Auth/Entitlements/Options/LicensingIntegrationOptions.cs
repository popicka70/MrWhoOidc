namespace MrWhoOidc.Auth.Entitlements.Options;

public sealed class LicensingIntegrationOptions
{
    public bool Enabled { get; init; } = false;
    public string BaseUrl { get; init; } = string.Empty;
    public int CacheTtlMinutes { get; init; } = 5;
    public int NegativeCacheTtlSeconds { get; init; } = 10;
    public string Audience { get; init; } = string.Empty;
}
