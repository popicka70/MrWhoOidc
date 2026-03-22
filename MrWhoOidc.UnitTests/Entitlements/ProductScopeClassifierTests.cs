using MrWhoOidc.Auth.Entitlements;

namespace MrWhoOidc.UnitTests.Entitlements;

[TestClass]
public sealed class ProductScopeClassifierTests
{
    [TestMethod]
    public void IsProductScope_ReturnsFalse_ForRegularApiScope()
    {
        Assert.IsFalse(ProductScopeClassifier.IsProductScope("api.read"));
    }

    [TestMethod]
    public void IsProductScope_ReturnsTrue_ForKnownProductScope()
    {
        Assert.IsTrue(ProductScopeClassifier.IsProductScope("mrwhopdf"));
        Assert.IsTrue(ProductScopeClassifier.IsProductScope("licensing.entitlements"));
    }
}