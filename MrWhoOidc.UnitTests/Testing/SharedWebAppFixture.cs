using Microsoft.AspNetCore.Mvc.Testing;
using MrWhoOidc.WebAuth;

namespace MrWhoOidc.UnitTests.Testing;

/// <summary>
/// Provides a shared WebApplicationFactory instance for integration tests within a test class.
/// This significantly improves test performance by avoiding factory creation overhead per test.
/// 
/// Usage:
/// 1. Add [ClassInitialize] and [ClassCleanup] methods that call Initialize/Cleanup
/// 2. Use SharedFactory property in tests instead of creating new factories
/// 
/// Example:
/// <code>
/// [TestClass]
/// public class MyIntegrationTests
/// {
///     private static SharedWebAppFixture _fixture = null!;
///     
///     [ClassInitialize]
///     public static void Initialize(TestContext _) => _fixture = new SharedWebAppFixture();
///     
///     [ClassCleanup]
///     public static void Cleanup() => _fixture.Dispose();
///     
///     [TestMethod]
///     public async Task MyTest()
///     {
///         using var client = _fixture.CreateClient();
///         // ...
///     }
/// }
/// </code>
/// </summary>
public sealed class SharedWebAppFixture : IDisposable
{
    private readonly Lazy<WebApplicationFactory<Program>> _factory;
    private bool _disposed;

    public SharedWebAppFixture()
    {
        _factory = new Lazy<WebApplicationFactory<Program>>(
            () => (WebApplicationFactory<Program>)TestWebAppFactory.CreateInMemory(),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Gets the shared WebApplicationFactory. Created lazily on first access.
    /// </summary>
    public WebApplicationFactory<Program> Factory => _factory.Value;

    /// <summary>
    /// Creates an HTTP client from the shared factory.
    /// </summary>
    public HttpClient CreateClient() => Factory.CreateClient();

    /// <summary>
    /// Creates an HTTP client with custom options from the shared factory.
    /// </summary>
    public HttpClient CreateClient(WebApplicationFactoryClientOptions options) => Factory.CreateClient(options);

    /// <summary>
    /// Gets the service provider from the shared factory.
    /// </summary>
    public IServiceProvider Services => Factory.Services;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_factory.IsValueCreated)
        {
            _factory.Value.Dispose();
        }
    }
}
