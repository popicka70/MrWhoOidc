using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers.Introspection;

namespace MrWhoOidc.UnitTests;

/// <summary>C10 of the 2026-10-04 assessment: introspection must be deny-by-default.</summary>
[TestClass]
public sealed class IntrospectionAudiencePolicyTests
{
    private static AudiencePolicy Policy(Dictionary<string, string[]>? global = null)
        => new(Options.Create(new AuthOptions { IntrospectionPermissions = global ?? new() }));

    [TestMethod]
    public void UnconfiguredCaller_CannotIntrospectForeignToken()
    {
        var caller = new ClientEntity { ClientId = "random-dcr-client" };
        Assert.IsFalse(Policy().IsClientAllowed(caller, new[] { "api" }, tokenClientId: "spa"));
    }

    [TestMethod]
    public void TokenWithoutAudience_DeniedUnlessCallerIsTokenClient()
    {
        var caller = new ClientEntity { ClientId = "rs" };
        Assert.IsFalse(Policy().IsClientAllowed(caller, Array.Empty<string>(), tokenClientId: "spa"));
        Assert.IsTrue(Policy().IsClientAllowed(caller, Array.Empty<string>(), tokenClientId: "rs"));
    }

    [TestMethod]
    public void CallerNamedInAudience_Allowed()
    {
        var caller = new ClientEntity { ClientId = "orders-api" };
        Assert.IsTrue(Policy().IsClientAllowed(caller, new[] { "billing-api", "orders-api" }, tokenClientId: "spa"));
    }

    [TestMethod]
    public void PerClientGrant_ChecksEveryAudience_NotOnlyTheFirst()
    {
        var caller = new ClientEntity { ClientId = "rs", IntrospectionAudiencesJson = "[\"api\"]" };
        Assert.IsTrue(Policy().IsClientAllowed(caller, new[] { "other", "api" }, tokenClientId: "spa"));
        Assert.IsFalse(Policy().IsClientAllowed(caller, new[] { "other" }, tokenClientId: "spa"));
    }

    [TestMethod]
    public void CorruptPerClientGrant_FailsClosed_EvenWithGlobalGrant()
    {
        var caller = new ClientEntity { ClientId = "rs", IntrospectionAudiencesJson = "not-json" };
        var policy = Policy(new() { ["rs"] = new[] { "api" } });
        Assert.IsFalse(policy.IsClientAllowed(caller, new[] { "api" }, tokenClientId: "spa"));
    }

    [TestMethod]
    public void GlobalGrant_Allowed()
    {
        var caller = new ClientEntity { ClientId = "rs" };
        Assert.IsTrue(Policy(new() { ["rs"] = new[] { "api" } }).IsClientAllowed(caller, new[] { "api" }, tokenClientId: "spa"));
    }

    [TestMethod]
    [DataRow(null, 0)]
    [DataRow("api", 1)]
    [DataRow("api orders", 2)]
    [DataRow("[\"api\",\"orders\"]", 2)]
    public void ParseStoredAudience(string? stored, int expected)
        => Assert.AreEqual(expected, AudiencePolicy.ParseStoredAudience(stored).Count);
}
