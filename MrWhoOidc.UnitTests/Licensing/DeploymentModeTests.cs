using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.UnitTests.Licensing;

/// <summary>
/// Unit tests for <see cref="DeploymentMode"/> enum and its extension methods.
/// </summary>
[TestClass]
public sealed class DeploymentModeTests
{
    [TestMethod]
    public void ToClaimValue_SingleTenant_ReturnsCorrectClaim()
    {
        var result = DeploymentMode.SingleTenant.ToClaimValue();

        Assert.AreEqual("single_tenant", result);
    }

    [TestMethod]
    public void ToClaimValue_MultiTenant_ReturnsCorrectClaim()
    {
        var result = DeploymentMode.MultiTenant.ToClaimValue();

        Assert.AreEqual("multi_tenant", result);
    }

    [TestMethod]
    public void ToClaimValue_UnknownValue_DefaultsToMultiTenant()
    {
        // Cast an invalid value to test default case
        var invalid = (DeploymentMode)999;

        var result = invalid.ToClaimValue();

        Assert.AreEqual("multi_tenant", result);
    }

    [TestMethod]
    public void FromClaimValue_SingleTenant_ParsesCorrectly()
    {
        var result = DeploymentModeExtensions.FromClaimValue("single_tenant");

        Assert.AreEqual(DeploymentMode.SingleTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_SingleTenant_CaseInsensitive()
    {
        var result = DeploymentModeExtensions.FromClaimValue("SINGLE_TENANT");

        Assert.AreEqual(DeploymentMode.SingleTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_MultiTenant_ParsesCorrectly()
    {
        var result = DeploymentModeExtensions.FromClaimValue("multi_tenant");

        Assert.AreEqual(DeploymentMode.MultiTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_Null_DefaultsToMultiTenant()
    {
        var result = DeploymentModeExtensions.FromClaimValue(null);

        Assert.AreEqual(DeploymentMode.MultiTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_Empty_DefaultsToMultiTenant()
    {
        var result = DeploymentModeExtensions.FromClaimValue(string.Empty);

        Assert.AreEqual(DeploymentMode.MultiTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_Whitespace_DefaultsToMultiTenant()
    {
        var result = DeploymentModeExtensions.FromClaimValue("   ");

        Assert.AreEqual(DeploymentMode.MultiTenant, result);
    }

    [TestMethod]
    public void FromClaimValue_UnknownValue_DefaultsToMultiTenant()
    {
        var result = DeploymentModeExtensions.FromClaimValue("unknown_mode");

        Assert.AreEqual(DeploymentMode.MultiTenant, result);
    }

    [TestMethod]
    public void Constants_HaveExpectedValues()
    {
        Assert.AreEqual("single_tenant", DeploymentModeExtensions.SingleTenantClaim);
        Assert.AreEqual("multi_tenant", DeploymentModeExtensions.MultiTenantClaim);
    }

    [TestMethod]
    public void RoundTrip_SingleTenant_PreservesValue()
    {
        var original = DeploymentMode.SingleTenant;

        var claim = original.ToClaimValue();
        var parsed = DeploymentModeExtensions.FromClaimValue(claim);

        Assert.AreEqual(original, parsed);
    }

    [TestMethod]
    public void RoundTrip_MultiTenant_PreservesValue()
    {
        var original = DeploymentMode.MultiTenant;

        var claim = original.ToClaimValue();
        var parsed = DeploymentModeExtensions.FromClaimValue(claim);

        Assert.AreEqual(original, parsed);
    }
}
