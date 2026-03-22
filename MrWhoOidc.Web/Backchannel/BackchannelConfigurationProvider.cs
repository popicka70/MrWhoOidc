using System.Collections.Concurrent;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MrWhoOidc.Web.Backchannel;

public interface IBackchannelConfigurationProvider
{
    Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, CancellationToken ct = default);
}

public sealed class BackchannelConfigurationProvider : IBackchannelConfigurationProvider
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _configurationManagers =
        new(StringComparer.Ordinal);

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authority))
            throw new ArgumentException("Authority is required.", nameof(authority));

        var metadataAddress = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var manager = _configurationManagers.GetOrAdd(
            metadataAddress,
            static address => new ConfigurationManager<OpenIdConnectConfiguration>(
                address,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever
                {
                    RequireHttps = address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                }));

        return manager.GetConfigurationAsync(ct);
    }
}
