using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Users;
using MrWhoOidc.UnitTests.TestDoubles;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;
using Moq;

namespace MrWhoOidc.UnitTests.Helpers;

public static class ExternalOidcTestServiceCollectionExtensions
{
    /// <summary>
    /// Registers minimal, safe defaults required for constructing the external OIDC handler pipeline in unit tests.
    /// Uses TryAdd to avoid overriding more realistic registrations in integration-style tests.
    /// </summary>
    public static IServiceCollection AddExternalOidcTestDefaults(this IServiceCollection services)
    {
        services.TryAddSingleton<IAuditSink, NoopAuditSink>();
        services.TryAddSingleton<IEmailConfirmationWorkflow, FakeEmailConfirmationWorkflow>();

        // External provisioning now depends on IClientStore; unit tests that don't register auth-core need a stub.
        services.TryAddSingleton<IClientStore, StubClientStore>();

        // External provisioning depends on IUserAccountProvisioner.
        services.TryAddScoped<RecordingUserAccountProvisioner>();
        services.TryAddScoped<IUserAccountProvisioner>(sp => sp.GetRequiredService<RecordingUserAccountProvisioner>());

        // RegistrationWorkflowService now depends on IRegistrationService.
        services.TryAddScoped<IRegistrationService>(_ => new Mock<IRegistrationService>().Object);

        services.TryAddScoped<ITenantDomainClaimService>(_ => new Mock<ITenantDomainClaimService>().Object);

        return services;
    }
}

