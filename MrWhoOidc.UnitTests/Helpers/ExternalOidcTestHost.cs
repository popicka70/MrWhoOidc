using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.UnitTests.Helpers;

public static class ExternalOidcTestHost
{
    public static (IServiceScope scope, IExternalOidcHandler handler, DefaultHttpContext ctx) Create(
        Action<IServiceCollection>? configureServices = null,
        Action<DefaultHttpContext>? configureContext = null,
        string? inMemoryDbName = null,
        bool useEphemeralDataProtectionProvider = false,
        bool useRecordingMetrics = false)
    {
        var services = new ServiceCollection();
        services.AddExternalOidcTestCore(
            inMemoryDbName: inMemoryDbName ?? ("ext-oidc-" + Guid.NewGuid().ToString("N")),
            useEphemeralDataProtectionProvider: useEphemeralDataProtectionProvider,
            useRecordingMetrics: useRecordingMetrics);

        services.AddExternalOidcTestDefaults();
        configureServices?.Invoke(services);

        services.AddExternalOidcHandler();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();

        var scoped = scope.ServiceProvider;
        var handler = scoped.GetRequiredService<IExternalOidcHandler>();

        var ctx = new DefaultHttpContext { RequestServices = scoped };
        scoped.GetRequiredService<IHttpContextAccessor>().HttpContext = ctx;

        // Some tests stash the scope on the context to keep it alive.
        ctx.Items["__scope"] = new RootedScope(provider, scope);

        configureContext?.Invoke(ctx);

        return (((RootedScope)ctx.Items["__scope"]!), handler, ctx);
    }

    private sealed class RootedScope : IServiceScope
    {
        private readonly ServiceProvider _provider;
        private readonly IServiceScope _inner;

        public RootedScope(ServiceProvider provider, IServiceScope inner)
        {
            _provider = provider;
            _inner = inner;
        }

        public IServiceProvider ServiceProvider => _inner.ServiceProvider;

        public void Dispose()
        {
            _inner.Dispose();
            _provider.Dispose();
        }
    }
}
