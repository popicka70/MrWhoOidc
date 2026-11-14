using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;

namespace MrWhoOidc.WebAuth.Infrastructure.DistributedState;

/// <summary>Describes the provider selected for <see cref="IDistributedStateStore"/>.</summary>
public sealed record DistributedStateRegistration(DistributedStateProvider Provider);

/// <summary>
/// Registers the distributed state backend selected by <c>DistributedState:Provider</c>.
/// The default is <see cref="DistributedStateProvider.Database"/>; Redis is opt-in and requires
/// <c>ConnectionStrings:redis</c>; Memory exists for tests and single-process development.
/// </summary>
public static class DistributedStateServiceCollectionExtensions
{
    public static DistributedStateRegistration AddMrWhoOidcDistributedState(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DistributedStateOptions>(
            configuration.GetSection(DistributedStateOptions.SectionName));

        var options = configuration.GetSection(DistributedStateOptions.SectionName)
            .Get<DistributedStateOptions>() ?? new DistributedStateOptions();
        var provider = options.ResolveProvider();

        switch (provider)
        {
            case DistributedStateProvider.Redis:
            {
                var multiplexer = services.AddMrWhoOidcRedis(configuration)
                    ?? throw new InvalidOperationException(
                        "DistributedState:Provider=Redis requires ConnectionStrings:redis (or ConnectionStrings__redis) to be set.");
                services.AddSingleton<IDistributedStateStore>(_ => new RedisDistributedStateStore(multiplexer));
                break;
            }

            case DistributedStateProvider.Database:
                services.AddSingleton<IDistributedStateStore, DatabaseDistributedStateStore>();
                if (!configuration.GetValue<bool>("Testing:DisableBackgroundServices"))
                {
                    services.AddHostedService<DistributedStateCleanupService>();
                }
                break;

            case DistributedStateProvider.Memory:
                services.AddSingleton<IDistributedStateStore, MemoryDistributedStateStore>();
                break;

            default:
                throw new InvalidOperationException($"Unsupported distributed state provider '{provider}'.");
        }

        // One cache adapter for HybridCache L2, sessions, login continuations and interaction
        // markers. Remove any earlier registration (for example AddDistributedMemoryCache) so the
        // selected provider is consistently used for every cache consumer.
        services.RemoveAll<IDistributedCache>();
        services.AddSingleton<IDistributedCache>(sp => new DistributedStateCache(
            sp.GetRequiredService<IDistributedStateStore>(),
            sp.GetRequiredService<IOptions<DistributedStateOptions>>()));

        return new DistributedStateRegistration(provider);
    }
}
