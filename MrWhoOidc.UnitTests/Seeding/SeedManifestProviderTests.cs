using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Seeding;
using MrWhoOidc.WebAuth.Seeding;

namespace MrWhoOidc.UnitTests.Seeding;

[TestClass]
public class SeedManifestProviderTests
{
    private Mock<ILogger<SeedManifestProvider>> _loggerMock = null!;

    [TestInitialize]
    public void Setup()
    {
        _loggerMock = new Mock<ILogger<SeedManifestProvider>>();
    }

    private SeedManifestProvider CreateProvider(SeedManifestOptions options)
    {
        var optionsMock = new Mock<IOptions<SeedManifestOptions>>();
        optionsMock.Setup(o => o.Value).Returns(options);
        return new SeedManifestProvider(optionsMock.Object, _loggerMock.Object);
    }

    [TestMethod]
    public async Task TryLoadAsync_ReturnsNull_WhenDisabled()
    {
        // Arrange
        var options = new SeedManifestOptions { Enabled = false };
        var provider = CreateProvider(options);

        // Act
        var result = await provider.TryLoadAsync();

        // Assert
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task TryLoadAsync_ReturnsManifest_WhenValidJsonProvided()
    {
        // Arrange
        var options = new SeedManifestOptions
        {
            Enabled = true,
            ManifestJson = "{\"AllowUpdates\": true}"
        };
        var provider = CreateProvider(options);

        // Act
        var result = await provider.TryLoadAsync();

        // Assert
        Assert.IsNotNull(result);
        // Assuming AllowUpdates exists in SeedManifest, but the main check is that it deserialized
    }

    [TestMethod]
    public async Task TryLoadAsync_ReturnsNullAndLogsWarning_WhenJsonIsMissingRequiredElementsOrDeserializesToNull()
    {
        // Arrange
        var options = new SeedManifestOptions
        {
            Enabled = true,
            ManifestJson = "null" // This makes JsonSerializer.Deserialize return null
        };
        var provider = CreateProvider(options);

        // Act
        var result = await provider.TryLoadAsync();

        // Assert
        Assert.IsNull(result);

        // Verify LogWarning was called
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Seed manifest was present but could not be deserialized (null).")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TryLoadAsync_ReturnsNullAndLogsError_WhenExceptionIsThrown()
    {
        // Arrange
        var options = new SeedManifestOptions
        {
            Enabled = true,
            ManifestBase64 = "invalid-base64-string!" // Causes Convert.FromBase64String to throw FormatException
        };
        var provider = CreateProvider(options);

        // Act
        var result = await provider.TryLoadAsync();

        // Assert
        Assert.IsNull(result);

        // Verify LogError was called
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to load seed manifest")),
                It.IsAny<FormatException>(), // specific exception
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [TestMethod]
    public async Task TryLoadAsync_ReturnsNullAndLogsError_WhenMalformedJsonProvided()
    {
        // Arrange
        var options = new SeedManifestOptions
        {
            Enabled = true,
            ManifestJson = "{\"invalid_json\"" // Causes JsonException during Deserialize
        };
        var provider = CreateProvider(options);

        // Act
        var result = await provider.TryLoadAsync();

        // Assert
        Assert.IsNull(result);

        // Verify LogError was called
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Failed to load seed manifest")),
                It.IsAny<JsonException>(), // specific exception
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
