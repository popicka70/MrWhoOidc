using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Security;

namespace MrWhoOidc.UnitTests;

[TestClass]
public class DPoPValidationResultTests
{
    [TestMethod]
    public void Default_DPoPValidationResult_HasExpectedValues()
    {
        // Act
        var result = default(DPoPValidationResult);

        // Assert
        Assert.IsFalse(result.Ok, "Default DPoPValidationResult should have Ok = false");
        Assert.IsNull(result.Jkt, "Default DPoPValidationResult should have Jkt = null");
        Assert.IsNull(result.Jti, "Default DPoPValidationResult should have Jti = null");
        Assert.IsNull(result.Iat, "Default DPoPValidationResult should have Iat = null");
        Assert.IsNull(result.Nonce, "Default DPoPValidationResult should have Nonce = null");
        Assert.IsNull(result.Error, "Default DPoPValidationResult should have Error = null");
    }
}
