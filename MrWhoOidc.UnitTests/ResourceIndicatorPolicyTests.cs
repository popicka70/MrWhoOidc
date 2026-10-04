using MrWhoOidc.Auth.Services.Authorization;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class ResourceIndicatorPolicyTests
{
    [TestMethod]
    [DataRow("https://api.example.com", null, true)]
    [DataRow("https://orders.example.com", "[\"https://orders.example.com\"]", true)]
    [DataRow("https://attacker.example.com", "[\"https://orders.example.com\"]", false)]
    [DataRow("https://orders.example.com", "not-json", false)]
    [DataRow("", null, false)]
    public void IsAllowed(string resource, string? perClient, bool expected)
    {
        var client = new ClientEntity { M2MAllowedAudiencesJson = perClient };
        Assert.AreEqual(expected, ResourceIndicatorPolicy.IsAllowed(client, new[] { "https://api.example.com" }, resource));
    }
}
