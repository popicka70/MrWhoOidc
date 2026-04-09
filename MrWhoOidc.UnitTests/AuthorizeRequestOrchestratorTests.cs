using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Licensing.Services;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class AuthorizeRequestOrchestratorTests
{
    [TestMethod]
    public async Task ResolveAndValidateAsync_RequestUri_DoesNotRequireAdvancedSecurityFeature()
    {
        var resolver = new StubAuthorizeRequestResolver(new AuthorizeRequestResolution(
            Request: new AuthorizeRequest(
                response_type: "code",
                client_id: "test_client",
                redirect_uri: "https://app/callback",
                scope: "openid",
                state: "state1",
                nonce: "nonce1"),
            ClientId: "test_client",
            ClientBucket: "test-client",
            Mode: "par",
            IsValid: true,
            Error: null,
            ErrorDescription: null,
            RequestSize: 42,
            ParId: "stub-par"));

        var featureService = new StubFeatureService(false);
        var orchestrator = CreateOrchestrator(resolver, featureService);
        var http = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["request_uri"] = "urn:ietf:params:oauth:request_uri:stub-par"
        });

        var (error, context) = await orchestrator.ResolveAndValidateAsync(http);

        Assert.IsNull(error);
        Assert.IsNotNull(context);
        Assert.AreEqual("par", context.Mode);
        Assert.AreEqual("urn:ietf:params:oauth:request_uri:stub-par", context.RequestUriRaw);
        Assert.AreEqual(1, resolver.CallCount);
        Assert.AreEqual(0, featureService.IsFeatureEnabledCallCount, "PAR request_uri resolution should not be license-gated.");
    }

    [TestMethod]
    public async Task ResolveAndValidateAsync_InlineRequestObject_StillRequiresAdvancedSecurityFeature()
    {
        var resolver = new StubAuthorizeRequestResolver(new AuthorizeRequestResolution(
            Request: null,
            ClientId: null,
            ClientBucket: "unknown",
            Mode: "jar",
            IsValid: false,
            Error: "invalid_request_object",
            ErrorDescription: "should not resolve",
            RequestSize: 0));

        var featureService = new StubFeatureService(false);
        var orchestrator = CreateOrchestrator(resolver, featureService);
        var http = CreateHttpContext(new Dictionary<string, string>
        {
            ["client_id"] = "test_client",
            ["request"] = "eyJhbGciOiJSUzI1NiJ9.e30.sig"
        });

        var (error, context) = await orchestrator.ResolveAndValidateAsync(http);

        Assert.IsNull(context);
        Assert.IsNotNull(error);
        Assert.AreEqual(1, featureService.IsFeatureEnabledCallCount, "Inline JWT request objects should remain feature-gated.");
        Assert.AreEqual(0, resolver.CallCount, "The resolver should not run when inline request-object gating rejects the request.");

        await error.ExecuteAsync(http);
        Assert.AreEqual(StatusCodes.Status403Forbidden, http.Response.StatusCode);
    }

    private static AuthorizeRequestOrchestrator CreateOrchestrator(IAuthorizeRequestResolver resolver, IFeatureService featureService)
    {
        return new AuthorizeRequestOrchestrator(
            resolver,
            featureService,
            Options.Create(new AuthOptions()),
            new OidcEndpointMetrics(),
            NullLogger<AuthorizeRequestOrchestrator>.Instance);
    }

    private static DefaultHttpContext CreateHttpContext(Dictionary<string, string> queryParams)
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var context = new DefaultHttpContext();
        context.RequestServices = services;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("test.example.com");
        context.Request.Path = "/authorize";
        context.Response.Body = new MemoryStream();
        context.Request.Query = new QueryCollection(queryParams.ToDictionary(
            kvp => kvp.Key,
            kvp => new Microsoft.Extensions.Primitives.StringValues(kvp.Value)));

        return context;
    }

    private sealed class StubAuthorizeRequestResolver(AuthorizeRequestResolution resolution) : IAuthorizeRequestResolver
    {
        public int CallCount { get; private set; }

        public Task<AuthorizeRequestResolution> ResolveAsync(
            IEnumerable<KeyValuePair<string, string>> queryParams,
            string? requestUriRaw,
            string? roJwtFromQuery,
            string issuer,
            CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(resolution);
        }
    }

    private sealed class StubFeatureService(bool enabled) : IFeatureService
    {
        public int IsFeatureEnabledCallCount { get; private set; }

        public Task<bool> IsFeatureEnabledAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
        {
            IsFeatureEnabledCallCount++;
            return Task.FromResult(enabled);
        }

        public Task<IReadOnlySet<string>> GetEnabledFeaturesAsync(Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        public Task RecordFeatureUsageAsync(string featureName, Guid? tenantId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<FeatureUsageMetric>> GetFeatureUsageAsync(Guid? tenantId = null, string? featureName = null, DateTimeOffset? fromDate = null, DateTimeOffset? toDate = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FeatureUsageMetric>>(Array.Empty<FeatureUsageMetric>());
    }
}