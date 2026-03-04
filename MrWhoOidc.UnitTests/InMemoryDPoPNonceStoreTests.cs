using Microsoft.VisualStudio.TestTools.UnitTesting;
using MrWhoOidc.Security;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class InMemoryDPoPNonceStoreTests
{
    [TestMethod]
    public async Task ValidateOrIssueAsync_MissingProvidedNonce_IssuesNewNonce()
    {
        // Arrange
        var store = new InMemoryDPoPNonceStore();
        var endpoint = "https://example.com/token";
        var clientIp = "127.0.0.1";
        var jkt = "test-jkt";

        // Act: missing provided nonce (null)
        var result = await store.ValidateOrIssueAsync(endpoint, clientIp, jkt, null);

        // Assert
        Assert.IsFalse(result.ok, "Should return false when provided nonce is missing");
        Assert.IsNotNull(result.nonce, "Should issue a new nonce");
        Assert.IsTrue(result.nonce.Length > 0);
    }

    [TestMethod]
    public async Task ValidateOrIssueAsync_IncorrectProvidedNonce_IssuesNewNonce()
    {
        // Arrange
        var store = new InMemoryDPoPNonceStore();
        var endpoint = "https://example.com/token";
        var clientIp = "127.0.0.1";
        var jkt = "test-jkt";

        // Issue initial nonce
        var initialResult = await store.ValidateOrIssueAsync(endpoint, clientIp, jkt, null);
        var initialNonce = initialResult.nonce;

        // Act: incorrect provided nonce
        var result = await store.ValidateOrIssueAsync(endpoint, clientIp, jkt, "wrong-nonce");

        // Assert
        Assert.IsFalse(result.ok, "Should return false when provided nonce is incorrect");
        Assert.IsNotNull(result.nonce, "Should issue a new nonce");
        Assert.AreNotEqual(initialNonce, result.nonce, "Should issue a DIFFERENT new nonce");
    }

    [TestMethod]
    public async Task ValidateOrIssueAsync_ValidProvidedNonce_ReturnsOk()
    {
        // Arrange
        var store = new InMemoryDPoPNonceStore();
        var endpoint = "https://example.com/token";
        var clientIp = "127.0.0.1";
        var jkt = "test-jkt";

        // Issue initial nonce
        var initialResult = await store.ValidateOrIssueAsync(endpoint, clientIp, jkt, null);
        var initialNonce = initialResult.nonce;

        // Act: valid provided nonce
        var result = await store.ValidateOrIssueAsync(endpoint, clientIp, jkt, initialNonce);

        // Assert
        Assert.IsTrue(result.ok, "Should return true when provided nonce is correct and not expired");
        Assert.AreEqual(initialNonce, result.nonce, "Should return the same nonce");
    }
}
