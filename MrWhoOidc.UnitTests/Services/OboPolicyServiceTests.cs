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
    public async Task EvaluateAsync_MalformedJson_SwallowsExceptionAndTreatsAsEmptyArray()
    {
        // Arrange
        var client = new ClientEntity
        {
            ClientId = "test_client",
            OboEnabled = true,
            OboAllowedCallersJson = "invalid_json_callers",
            OboAllowedTargetAudiencesJson = "invalid_json_targets",
            OboAllowedSourceAudiencesJson = "invalid_json_sources",
            OboAllowedScopesJson = "invalid_json_scopes",
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
