using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Token;
using MrWhoOidc.UnitTests.Testing;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// The pieces of the OP session store (F1) have to be wired in the container: the session service, the cookie
/// validator's collaborators and the token exchangers (which take the session service as an optional
/// dependency).
/// </summary>
[TestClass]
public sealed class OpSessionDependencyWiringTests
{
    [TestMethod]
    public void TheSessionStore_IsRegistered_AndTheExchangersGetIt()
    {
        using var factory = TestWebAppFactory.CreateInMemory();

        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        Assert.IsNotNull(provider.GetService<IOpSessionService>(), "the session service must be registered");

        var refreshExchanger = provider.GetRequiredService<IRefreshTokenExchanger>();
        Assert.IsNotNull(refreshExchanger, "the refresh exchanger must resolve with the session service");

        var codeExchanger = provider.GetRequiredService<IAuthorizationCodeExchanger>();
        Assert.IsNotNull(codeExchanger, "the authorization-code exchanger must resolve");
    }
}
