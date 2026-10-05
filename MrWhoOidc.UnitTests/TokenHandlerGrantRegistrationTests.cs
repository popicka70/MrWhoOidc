using MrWhoOidc.WebAuth.Handlers;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class TokenHandlerGrantRegistrationTests
{
    [TestMethod]
    [DataRow(null, "client_credentials", false)]
    [DataRow(null, "urn:ietf:params:oauth:grant-type:device_code", false)]
    [DataRow(null, "urn:openid:params:grant-type:ciba", false)]
    [DataRow(null, "urn:ietf:params:oauth:grant-type:token-exchange", false)]
    [DataRow(null, "authorization_code", true)]
    [DataRow(null, "refresh_token", true)]
    [DataRow("", "client_credentials", false)]
    [DataRow("[\"authorization_code\"]", "authorization_code", true)]
    [DataRow("[\"authorization_code\"]", "refresh_token", true)]
    [DataRow("[\"authorization_code\"]", "client_credentials", false)]
    [DataRow("[\"client_credentials\"]", "refresh_token", false)]
    [DataRow("[]", "authorization_code", false)]
    [DataRow("not-json", "authorization_code", false)]
    public void IsGrantTypeRegistered(string? registered, string grant, bool expected)
    {
        var client = new ClientEntity { GrantTypesJson = registered };
        Assert.AreEqual(expected, TokenHandler.IsGrantTypeRegistered(client, grant));
    }
}
