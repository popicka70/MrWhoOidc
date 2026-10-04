using System.Security.Claims;
using MrWhoOidc.KeyGen.Configuration;
using MrWhoOidc.KeyGen.Security;

namespace MrWhoOidc.KeyGen.Tests;

[TestClass]
public sealed class KeyGenAuthOptionsTests
{
    [TestMethod]
    public void Empty_options_are_invalid_in_every_environment()
    {
        var options = new KeyGenAuthOptions();

        Assert.IsNotEmpty(options.Validate(isDevelopment: false));
        Assert.IsNotEmpty(options.Validate(isDevelopment: true));
    }

    [TestMethod]
    public void Authority_must_be_https()
    {
        var options = new KeyGenAuthOptions { Authority = "http://idp.example.com", ClientId = "keygen" };

        var errors = options.Validate(isDevelopment: false);

        Assert.HasCount(1, errors);
        StringAssert.Contains(errors[0], "https://");
    }

    [TestMethod]
    public void Configured_oidc_is_valid_and_defaults_to_the_platform_admin_role()
    {
        var options = new KeyGenAuthOptions { Authority = "https://idp.example.com/t/default", ClientId = "keygen" };

        Assert.IsEmpty(options.Validate(isDevelopment: false));
        Assert.AreEqual("platform-admin", options.RequiredRole);
        Assert.AreEqual("roles", options.RoleClaimType);
    }

    [TestMethod]
    public void Development_opt_out_is_valid_only_in_development()
    {
        var options = new KeyGenAuthOptions { DisableInDevelopment = true };

        Assert.IsEmpty(options.Validate(isDevelopment: true));
        Assert.IsNotEmpty(options.Validate(isDevelopment: false));
    }
}

[TestClass]
public sealed class IssuerIdentityTests
{
    [TestMethod]
    public void Anonymous_principal_has_no_issuer()
    {
        Assert.IsNull(IssuerIdentity.Describe(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.IsNull(IssuerIdentity.Describe(null));
    }

    [TestMethod]
    public void Name_email_and_subject_are_combined()
    {
        var user = Principal(("sub", "42"), ("name", "Jane Admin"), ("email", "jane@example.com"));

        Assert.AreEqual("Jane Admin <jane@example.com> (sub: 42)", IssuerIdentity.Describe(user));
    }

    [TestMethod]
    public void Subject_alone_is_enough()
    {
        Assert.AreEqual("(sub: 42)", IssuerIdentity.Describe(Principal(("sub", "42"))));
    }

    [TestMethod]
    public void Long_values_are_truncated_to_the_column_length_but_keep_the_subject()
    {
        var user = Principal(("sub", "subject-1"), ("name", new string('x', 500)));

        var issuer = IssuerIdentity.Describe(user)!;

        Assert.AreEqual(IssuerIdentity.MaxLength, issuer.Length);
        Assert.EndsWith("(sub: subject-1)", issuer);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), "test", "name", "roles"));
}
