using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Authorization;
using MrWhoOidc.Client.Discovery;
using MrWhoOidc.Client.Http;
using MrWhoOidc.Client.Jwks;
using MrWhoOidc.Client.Logout;
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
        services.TryAddSingleton<IMrWhoOnBehalfOfManager, MrWhoOnBehalfOfManager>();
        services.TryAddSingleton<IMrWhoClientCredentialsManager, MrWhoClientCredentialsManager>();
        services.TryAddSingleton<IMrWhoLogoutManager, MrWhoLogoutManager>();

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

        // Configure certificate validation bypass for development scenarios
        httpClientBuilder.ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var optionsMonitor = sp.GetRequiredService<IOptionsMonitor<MrWhoOidcClientOptions>>();
            var opts = optionsMonitor.CurrentValue;
            var handler = new HttpClientHandler();
            if (opts.DangerousAcceptAnyServerCertificateValidator)
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("MrWhoOidc.Client.Http");
                logger.LogWarning("DangerousAcceptAnyServerCertificateValidator is enabled - SSL certificate validation is bypassed. DO NOT use in production!");
                handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
            }
            return handler;
        });

        services.TryAddSingleton<IHttpMessageHandlerBuilderFilter, MrWhoOidcLoggingFilter>();

        return services;
    }

    public static IHttpClientBuilder AddMrWhoOnBehalfOfTokenHandler(this IHttpClientBuilder builder, string registrationName, Func<IServiceProvider, CancellationToken, ValueTask<string?>> subjectTokenAccessor)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(registrationName);
        ArgumentNullException.ThrowIfNull(subjectTokenAccessor);

        return builder.AddHttpMessageHandler(sp =>
        {
            Func<CancellationToken, ValueTask<string?>> resolver = ct => subjectTokenAccessor(sp, ct);
            return new OnBehalfOfAccessTokenHandler(
                sp.GetRequiredService<IMrWhoOnBehalfOfManager>(),
                registrationName,
                resolver,
                sp.GetRequiredService<ILogger<OnBehalfOfAccessTokenHandler>>());
        });
    }

    public static IHttpClientBuilder AddMrWhoClientCredentialsTokenHandler(this IHttpClientBuilder builder, string registrationName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(registrationName);

        return builder.AddHttpMessageHandler(sp =>
            new ClientCredentialsAccessTokenHandler(
                sp.GetRequiredService<IMrWhoClientCredentialsManager>(),
                registrationName,
                sp.GetRequiredService<ILogger<ClientCredentialsAccessTokenHandler>>()));
    }
}
