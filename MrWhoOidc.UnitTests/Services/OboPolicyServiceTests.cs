using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth;
using MrWhoOidc.Auth.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.UnitTests.Services;

[TestClass]
public class OboPolicyServiceTests
{
    private OboPolicyService? _service;
    private AuthDbContext? _db;
    private Mock<IOptions<AuthOptions>>? _authOptionsMock;

    [TestInitialize]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new AuthDbContext(options);

        _authOptionsMock = new Mock<IOptions<AuthOptions>>();
        _authOptionsMock.Setup(x => x.Value).Returns(new AuthOptions());

        _service = new OboPolicyService(_db, _authOptionsMock.Object);
    }

    [TestMethod]
    [DataRow(nameof(ClientEntity.OboAllowedCallersJson))]
    [DataRow(nameof(ClientEntity.OboAllowedTargetAudiencesJson))]
    [DataRow(nameof(ClientEntity.OboAllowedSourceAudiencesJson))]
    [DataRow(nameof(ClientEntity.OboAllowedScopesJson))]
    public async Task EvaluateAsync_MalformedJson_Denies(string property)
    {
        // R25: an allow-list that does not parse used to read as empty, i.e. unrestricted.
        var client = new ClientEntity
        {
            ClientId = "test_client",
            OboEnabled = true,
        };
        typeof(ClientEntity).GetProperty(property)!.SetValue(client, "invalid_json");
        _db!.Clients.Add(client);
        await _db.SaveChangesAsync();

        _authOptionsMock!.Setup(x => x.Value).Returns(new AuthOptions { ApiAudiences = new[] { "target" } });

        // Act
        var result = await _service!.EvaluateAsync(
            "test_client",
            "source",
            "target",
            new[] { "scope1" },
            new[] { "scope1" },
            DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert
        Assert.IsFalse(result.ok);
        Assert.AreEqual("unauthorized_client", result.error);
    }

    [TestMethod]
    public async Task EvaluateAsync_NoTargetAudienceConfiguredAnywhere_Denies()
    {
        // Neither a per-client list nor global ApiAudiences: there is no audience the caller may target.
        _db!.Clients.Add(new ClientEntity { ClientId = "test_client", OboEnabled = true });
        await _db.SaveChangesAsync();
        _authOptionsMock!.Setup(x => x.Value).Returns(new AuthOptions { ApiAudiences = [] });

        var result = await _service!.EvaluateAsync("test_client", null, "any-api", ["scope1"], ["scope1"], DateTimeOffset.UtcNow.AddHours(1));

        Assert.IsFalse(result.ok);
        Assert.AreEqual("invalid_target", result.error);
    }

    [TestMethod]
    public async Task EvaluateAsync_NullEmptyOrWhitespaceJson_TreatsAsEmptyArray()
    {
        // Arrange
        var client = new ClientEntity
        {
            ClientId = "test_client",
            OboEnabled = true,
            OboAllowedCallersJson = null,
            OboAllowedTargetAudiencesJson = "",
            OboAllowedSourceAudiencesJson = "   ",
            OboAllowedScopesJson = null,
        };
        _db!.Clients.Add(client);
        await _db.SaveChangesAsync();

        _authOptionsMock!.Setup(x => x.Value).Returns(new AuthOptions { ApiAudiences = new[] { "target" } });

        // Act
        var result = await _service!.EvaluateAsync(
            "test_client",
            "source",
            "target",
            new[] { "scope1" },
            new[] { "scope1" },
            DateTimeOffset.UtcNow.AddHours(1)
        );

        // Assert
        Assert.IsTrue(result.ok);
        Assert.IsNull(result.error);
        CollectionAssert.AreEquivalent(new[] { "scope1" }, result.scopes);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db!.Database.EnsureDeleted();
        _db.Dispose();
    }
}
