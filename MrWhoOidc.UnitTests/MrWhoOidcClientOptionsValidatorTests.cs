using Microsoft.Extensions.Options;
using MrWhoOidc.Client.Options;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class MrWhoOidcClientOptionsValidatorTests
{
    private readonly MrWhoOidcClientOptionsValidator _validator = new();

    [TestMethod]
    public void Validate_Succeeds_ForConfidentialClientWithSecret()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.AreEqual(ValidateOptionsResult.Success, result);
    }

    [TestMethod]
    public void Validate_Fails_WhenSecretMissing()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            PublicClient = false
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "Confidential clients must configure either ClientSecret or ClientAssertion.");
    }

    [TestMethod]
    public void Validate_Fails_ForNonHttpsIssuer_WhenRequired()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "http://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "Issuer must be an absolute HTTPS URI");
    }

    [TestMethod]
    public void Validate_Fails_ForInvalidOnBehalfOfRegistration()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        options.OnBehalfOf["api"] = new OnBehalfOfRegistration { SubjectTokenType = string.Empty };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "On-behalf-of registration 'api' must specify SubjectTokenType.");
    }

    [TestMethod]
    public void Validate_Fails_ForInvalidClientCredentialsRegistration()
    {
        var options = new MrWhoOidcClientOptions
        {
            Issuer = "https://issuer.example.com",
            ClientId = "client",
            ClientSecret = "secret"
        };

        options.ClientCredentials["service"] = new ClientCredentialsRegistration();

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(string.Join(';', result.Failures!), "Client credentials registration 'service' must configure Scopes, Resource, or Audience.");
    }
}
