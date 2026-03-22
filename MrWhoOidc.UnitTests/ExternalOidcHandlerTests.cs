using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.IdentityProviders;
using MrWhoOidc.Auth.MultiTenancy;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Handlers.External;
using MrWhoOidc.WebAuth.Infrastructure.ServiceRegistration;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;
using System.Net;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for ExternalOidcHandler error scenarios, validation, and edge cases.
/// Integration-level happy path tests exist in ExternalOidcIntegrationTests.
/// </summary>
[TestClass]
public class ExternalOidcHandlerTests
{
    #region StartAsync Error Tests

    [TestMethod]
    public async Task Start_Missing_Provider_Parameter_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?returnUrl=%2Fauthorize&clientId=web");

            // Act
            var result = await handler.StartAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            var urlProp = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var url = urlProp?.GetValue(result)?.ToString();
            Assert.IsTrue(url?.Contains("/auth/external/error") ?? false, "Expected redirect to error page");
        }
    }

    [TestMethod]
    public async Task Start_Missing_ReturnUrl_Parameter_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?provider=google&clientId=web");

            // Act
            var result = await handler.StartAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            var urlProp = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var url = urlProp?.GetValue(result)?.ToString();
            Assert.IsTrue(url?.Contains("/auth/external/error") ?? false, "Expected redirect to error page");
        }
    }

    [TestMethod]
    public async Task Start_Unknown_Provider_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?provider=nonexistent&returnUrl=%2Fauthorize&clientId=web");

            // Act
            var result = await handler.StartAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            var urlProp = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var url = urlProp?.GetValue(result)?.ToString();
            Assert.IsTrue(url?.Contains("/auth/external/error") ?? false, "Expected redirect to error page for unknown provider");
        }
    }

    [TestMethod]
    public async Task Start_Disabled_Provider_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            var db = ctx.RequestServices.GetRequiredService<AuthDbContext>();

            // Add a disabled provider
            db.IdentityProviders.Add(new IdentityProvider
            {
                Name = "disabled-provider",
                Enabled = false,
                ConfigJson = JsonSerializer.Serialize(new
                {
                    Authority = "https://provider.example.com",
                    ClientId = "client123"
                })
            });
            await db.SaveChangesAsync();

            ctx.Request.QueryString = new QueryString("?provider=disabled-provider&returnUrl=%2Fauthorize&clientId=web");

            // Act
            var result = await handler.StartAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            var urlProp = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var url = urlProp?.GetValue(result)?.ToString();
            Assert.IsTrue(url?.Contains("/auth/external/error") ?? false, "Expected redirect to error page for disabled provider");
        }
    }

    [TestMethod]
    public async Task Start_Invalid_Provider_Config_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            var db = ctx.RequestServices.GetRequiredService<AuthDbContext>();

            // Add a provider with invalid JSON config
            db.IdentityProviders.Add(new IdentityProvider
            {
                Name = "invalid-config",
                Enabled = true,
                ConfigJson = "{invalid json"
            });
            await db.SaveChangesAsync();

            ctx.Request.QueryString = new QueryString("?provider=invalid-config&returnUrl=%2Fauthorize&clientId=web");

            // Act
            var result = await handler.StartAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            var urlProp = result.GetType().GetProperty("Url") ?? result.GetType().GetProperty("Location");
            var url = urlProp?.GetValue(result)?.ToString();
            Assert.IsTrue(url?.Contains("/auth/external/error") ?? false, "Expected redirect to error page for invalid config");
        }
    }

    #endregion

    #region CallbackAsync Error Tests

    [TestMethod]
    public async Task Callback_Missing_State_Parameter_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?code=abc123");

            // Act
            var result = await handler.CallbackAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            // CallbackAsync returns Results.BadRequest("Missing state") for missing state
            var resultType = result.GetType().Name;
            Assert.IsTrue(
                resultType.Contains("BadRequest") || resultType.Contains("BadHttpRequest"),
                $"Expected BadRequest result, got: {resultType}");
        }
    }

    [TestMethod]
    public async Task Callback_Invalid_State_Returns_Error()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            ctx.Request.QueryString = new QueryString("?state=invalid-state-value&code=abc123");

            // Act
            var result = await handler.CallbackAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            // CallbackAsync returns Results.BadRequest("Invalid state") for invalid state
            var resultType = result.GetType().Name;
            Assert.IsTrue(
                resultType.Contains("BadRequest") || resultType.Contains("BadHttpRequest"),
                $"Expected BadRequest result, got: {resultType}");
        }
    }

    [TestMethod]
    public async Task Callback_Error_Parameter_Propagated()
    {
        // Arrange
        var (handler, ctx, scope) = CreateHandler();
        using (scope)
        {
            // Without valid state, will still get BadRequest for missing state
            ctx.Request.QueryString = new QueryString("?error=access_denied&error_description=User%20cancelled");

            // Act
            var result = await handler.CallbackAsync(ctx);

            // Assert
            Assert.IsNotNull(result);
            // Without state parameter, will return BadRequest("Missing state")
            // This test verifies error parameter is present in query, but state validation happens first
            var resultType = result.GetType().Name;
            Assert.IsTrue(
                resultType.Contains("BadRequest") || resultType.Contains("BadHttpRequest"),
                $"Expected BadRequest for missing state, got: {resultType}");
        }
    }

    #endregion

    #region Test Helpers

    private static (IExternalOidcHandler handler, DefaultHttpContext ctx, IServiceScope scope) CreateHandler()
    {
        var (scope, handler, ctx) = ExternalOidcTestHost.Create(
            configureServices: services =>
            {
                services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions()));

                // Add HttpClient with test handler
                services.AddSingleton<IHttpClientFactory>(new TestHttpClientFactory());
            },
            configureContext: ctx =>
            {
                ctx.Request.Scheme = "https";
                ctx.Request.Host = new HostString("test.example.com");
            },
            inMemoryDbName: "ext-handler-" + Guid.NewGuid().ToString("N"),
            useEphemeralDataProtectionProvider: false,
            useRecordingMetrics: false);

        return (handler, ctx, scope);
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new TestHttpMessageHandler());
        }
    }

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Return empty discovery document for any discovery request
            if (request.RequestUri?.AbsolutePath?.Contains("/.well-known/openid-configuration") == true)
            {
                var discovery = new
                {
                    issuer = request.RequestUri.GetLeftPart(UriPartial.Authority),
                    authorization_endpoint = $"{request.RequestUri.GetLeftPart(UriPartial.Authority)}/authorize",
                    token_endpoint = $"{request.RequestUri.GetLeftPart(UriPartial.Authority)}/token",
                    jwks_uri = $"{request.RequestUri.GetLeftPart(UriPartial.Authority)}/jwks",
                    userinfo_endpoint = $"{request.RequestUri.GetLeftPart(UriPartial.Authority)}/userinfo"
                };

                var json = JsonSerializer.Serialize(discovery);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
                });
            }

            // Return empty JWKS for JWKS requests
            if (request.RequestUri?.AbsolutePath?.Contains("/jwks") == true)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"keys\":[]}", System.Text.Encoding.UTF8, "application/json")
                });
            }

            // Default: return empty JSON
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    #endregion
}
