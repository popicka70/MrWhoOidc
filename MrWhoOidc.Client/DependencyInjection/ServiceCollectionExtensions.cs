using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Authorization;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Options;
using MrWhoOidc.Client.Tokens;
using MrWhoOidc.Security;

namespace MrWhoOidc.Client.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMrWhoOidcClient(this IServiceCollection services, IConfiguration configuration, string? sectionName = null)
    {
        sectionName ??= MrWhoOidcClientDefaults.DefaultSectionName;
        services.AddOptions<MrWhoOidcClientOptions>()
            .Bind(configuration.GetSection(sectionName))
            .ValidateOnStart();

        return AddMrWhoOidcClientCore(services);
    }

    public static IServiceCollection AddMrWhoOidcClient(this IServiceCollection services, Action<MrWhoOidcClientOptions> configure)
    {
        services.AddOptions<MrWhoOidcClientOptions>()
            .Configure(configure)
            .ValidateOnStart();

        return AddMrWhoOidcClientCore(services);
    }

    private static IServiceCollection AddMrWhoOidcClientCore(IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MrWhoOidcClientOptions>, MrWhoOidcClientOptionsValidator>());

        services.AddMemoryCache();

        services.TryAddSingleton<IDPoPKeyStore, EphemeralDpopKeyStore>();
        services.TryAddSingleton<IDPoPProofGenerator, JwtDpopProofGenerator>();

        services.TryAddSingleton<IMrWhoDiscoveryClient, MrWhoDiscoveryClient>();
        services.TryAddSingleton<IMrWhoJwksCache, MrWhoJwksCache>();
        services.TryAddSingleton<IMrWhoTokenClient, MrWhoTokenClient>();
        services.TryAddSingleton<IMrWhoAuthorizationManager, MrWhoAuthorizationManager>();

        var httpClientBuilder = services.AddHttpClient(MrWhoOidcClientDefaults.DefaultHttpClientName);
        httpClientBuilder.AddStandardResilienceHandler();
        httpClientBuilder.ConfigureHttpClient((sp, http) =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<MrWhoOidcClientOptions>>();
            var opts = optionsMonitor.CurrentValue;
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MrWhoOidc.Client.Http");
            http.Timeout = opts.BackchannelTimeout;
            if (Uri.TryCreate(opts.Issuer, UriKind.Absolute, out var issuer))
            {
                http.BaseAddress = issuer;
            }
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MrWhoOidc.Client/0.1.0");
            logger.LogDebug("Configured HttpClient for MrWhoOidc client with timeout {Timeout}", http.Timeout);
        });

        services.TryAddSingleton<IHttpMessageHandlerBuilderFilter, MrWhoOidcLoggingFilter>();

        return services;
    }
}
