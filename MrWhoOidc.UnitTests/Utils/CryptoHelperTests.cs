using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.UnitTests.Utils;

[TestClass]
public sealed class CryptoHelperTests
{
    [TestMethod]
    public void GenerateSecureRandomString_ReturnsExpectedLength()
    {
        // Arrange
        int length = 32;

        // Act
        string result = CryptoHelper.GenerateSecureRandomString(length);

        // Assert
        Assert.AreEqual(length, result.Length);
    }

    [TestMethod]
    public void GenerateSecureRandomString_UsesAllowedCharacters()
    {
        // Arrange
        int length = 1000;
        string choices = "ABC";

        // Act
        string result = CryptoHelper.GenerateSecureRandomString(length, choices);

        // Assert
        Assert.AreEqual(length, result.Length);
        foreach (char c in result)
        {
            Assert.IsTrue(choices.Contains(c), $"Character {c} is not in choices {choices}");
        }
    }

    [TestMethod]
    public void GenerateSecureRandomString_ProducesDifferentStrings()
    {
        // Arrange
        int length = 32;

        // Act
        string result1 = CryptoHelper.GenerateSecureRandomString(length);
        string result2 = CryptoHelper.GenerateSecureRandomString(length);

        // Assert
        Assert.AreNotEqual(result1, result2);
    }
}
