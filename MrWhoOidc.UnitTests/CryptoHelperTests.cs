using System.Security.Cryptography;
using MrWhoOidc.Auth.Utils;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class CryptoHelperTests
{
    [TestMethod]
    public void GenerateRandomString_ReturnsCorrectLength()
    {
        // Arrange
        const int length = 16;
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";

        // Act
        var result = CryptoHelper.GenerateSecureRandomString(length, chars);

        // Assert
        Assert.AreEqual(length, result.Length);
    }

    [TestMethod]
    public void GenerateRandomString_ReturnsOnlyAllowedCharacters()
    {
        // Arrange
        const int length = 100;
        const string chars = "ABC";

        // Act
        var result = CryptoHelper.GenerateSecureRandomString(length, chars);

        // Assert
        foreach (var c in result)
        {
            Assert.IsTrue(chars.Contains(c), $"Character '{c}' is not in allowed set '{chars}'");
        }
    }

    [TestMethod]
    public void GenerateRandomString_ProducesDifferentStrings()
    {
        // Arrange
        const int length = 32;
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";

        // Act
        var result1 = CryptoHelper.GenerateSecureRandomString(length, chars);
        var result2 = CryptoHelper.GenerateSecureRandomString(length, chars);

        // Assert
        Assert.AreNotEqual(result1, result2);
    }
}
