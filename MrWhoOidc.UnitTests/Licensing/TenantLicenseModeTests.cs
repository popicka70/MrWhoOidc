using MrWhoOidc.Auth.Licensing.Models;

namespace MrWhoOidc.UnitTests.Licensing;

/// <summary>
/// Unit tests for <see cref="TenantLicenseMode"/> enum.
/// </summary>
[TestClass]
public sealed class TenantLicenseModeTests
{
    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Assertions", "MSTEST0032:Review or remove assertion", Justification = "Intentional constant value check to catch accidental changes")]
    public void InheritPlatform_HasExpectedValue()
    {
        Assert.AreEqual(0, (int)TenantLicenseMode.InheritPlatform);
    }

    [TestMethod]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Assertions", "MSTEST0032:Review or remove assertion", Justification = "Intentional constant value check to catch accidental changes")]
    public void Sublicense_HasExpectedValue()
    {
        Assert.AreEqual(1, (int)TenantLicenseMode.Sublicense);
    }

    [TestMethod]
    public void DefaultValue_IsInheritPlatform()
    {
        var defaultMode = default(TenantLicenseMode);

        Assert.AreEqual(TenantLicenseMode.InheritPlatform, defaultMode);
    }

    [TestMethod]
    public void AllValues_CanBeParsed()
    {
        foreach (var value in Enum.GetValues<TenantLicenseMode>())
        {
            var name = value.ToString();
            Assert.IsTrue(Enum.TryParse<TenantLicenseMode>(name, out var parsed));
            Assert.AreEqual(value, parsed);
        }
    }
}
