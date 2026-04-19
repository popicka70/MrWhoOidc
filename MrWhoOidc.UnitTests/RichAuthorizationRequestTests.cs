using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.UnitTests.TestSupport;
using System.Text.Json;

namespace MrWhoOidc.UnitTests;

/// <summary>
/// Tests for RFC 9396 Rich Authorization Requests (authorization_details parameter).
/// </summary>
[TestClass]
public sealed class RichAuthorizationRequestTests
{
    private static AuthDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AuthDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static (AuthorizeRequestValidator validator, AuthDbContext db, Mock<IClientStore> clientsMock) CreateValidator(MrWhoOidc.Auth.Persistence.Client? client = null)
    {
        var db = CreateDb();
        var clientsMock = new Mock<IClientStore>();
        client ??= new MrWhoOidc.Auth.Persistence.Client
        {
            Id = Guid.NewGuid(),
            ClientId = "test_client",
            ClientName = "Test",
            TokenEndpointAuthMethod = "client_secret_basic"
            // AllowedLoginRedirectUrisJson = null means no restriction on redirect URIs
        };
        clientsMock
            .Setup(x => x.FindByClientIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client);

        var validator = new AuthorizeRequestValidator(db, clientsMock.Object, NullLogger<AuthorizeRequestValidator>.Instance);
        return (validator, db, clientsMock);
    }

    private static AuthorizeRequest ValidBaseRequest(string? authorizationDetails = null, string? nonce = "n1") => new(
        response_type: "code",
        client_id: "test_client",
        redirect_uri: "https://app/callback",
        scope: "openid",
        nonce: nonce,
        code_challenge: "aaabbbcccdddeeefffaaabbbcccdddeeefffaaabbbcc",
        code_challenge_method: "S256",
        authorization_details: authorizationDetails
    );

    // ── Positive path ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_WithValid_AuthorizationDetails_Succeeds()
    {
        var (validator, _, _) = CreateValidator();
        var json = """[{"type":"payment","instructedAmount":{"currency":"EUR","amount":"123.50"}}]""";
        var result = await validator.ValidateAsync(ValidBaseRequest(json));

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.IsNotNull(result.AuthorizationDetailsJson);

        using var doc = JsonDocument.Parse(result.AuthorizationDetailsJson);
        Assert.AreEqual(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.AreEqual(1, doc.RootElement.GetArrayLength());
        Assert.AreEqual("payment", doc.RootElement[0].GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task Validate_WithMultipleTypes_Succeeds()
    {
        var (validator, _, _) = CreateValidator();
        var json = """[{"type":"account_information"},{"type":"payment","currency":"EUR"}]""";
        var result = await validator.ValidateAsync(ValidBaseRequest(json));

        Assert.IsTrue(result.IsValid);
        Assert.IsNotNull(result.AuthorizationDetailsJson);
        using var doc = JsonDocument.Parse(result.AuthorizationDetailsJson);
        Assert.AreEqual(2, doc.RootElement.GetArrayLength());
    }

    [TestMethod]
    public async Task Validate_Without_AuthorizationDetails_Succeeds()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest());

        Assert.IsTrue(result.IsValid);
        Assert.IsNull(result.AuthorizationDetailsJson);
    }

    [TestMethod]
    public async Task Validate_CodeFlowWithoutNonce_Succeeds()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest(nonce: null));

        Assert.IsTrue(result.IsValid, result.ErrorDescription);
        Assert.IsNull(result.Nonce);
    }

    // ── Negative path ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Validate_InvalidJson_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("not-json"));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Validate_JsonObject_NotArray_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("""{"type":"payment"}"""));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
        StringAssert.Contains(result.ErrorDescription!, "array");
    }

    [TestMethod]
    public async Task Validate_EmptyArray_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("[]"));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
        StringAssert.Contains(result.ErrorDescription!, "empty");
    }

    [TestMethod]
    public async Task Validate_ElementMissingType_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("""[{"amount":"100"}]"""));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
        StringAssert.Contains(result.ErrorDescription!, "type");
    }

    [TestMethod]
    public async Task Validate_ElementTypeIsNotString_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("""[{"type": 42}]"""));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Validate_EmptyTypeString_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("""[{"type":""}]"""));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    [TestMethod]
    public async Task Validate_ArrayContainsNonObject_Returns400()
    {
        var (validator, _, _) = CreateValidator();
        var result = await validator.ValidateAsync(ValidBaseRequest("""["payment"]"""));

        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_request", result.Error);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task AuthorizationCodeService_Persists_AuthorizationDetails()
    {
        using var db = CreateDb();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var settingsService = new MockTenantSettingsService();
        var svc = new AuthorizationCodeService(db, meta, tenantAccessor, settingsService);

        var authDetails = """[{"type":"payment","amount":"50.00"}]""";
        var valid = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            RedirectUri: "https://app/cb",
            Scopes: new[] { "openid" },
            Nonce: "n",
            State: "s",
            AuthorizationDetailsJson: authDetails
        );

        var (ok, _, _, code) = await svc.IssueAsync(valid, Guid.NewGuid());

        Assert.IsTrue(ok);
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync();
        Assert.IsNotNull(entity);
        Assert.AreEqual(authDetails, entity.AuthorizationDetailsJson);
    }

    [TestMethod]
    public async Task AuthorizationCodeService_NullAuthorizationDetails_PersistsNull()
    {
        using var db = CreateDb();
        var meta = new InMemoryAuthorizationCodeMetadataStore();
        var tenantAccessor = MockTenantAccessor.CreateWithDefaultTenant();
        var settingsService = new MockTenantSettingsService();
        var svc = new AuthorizationCodeService(db, meta, tenantAccessor, settingsService);

        var valid = new AuthorizeValidationResult(
            IsValid: true,
            ClientId: "c1",
            RedirectUri: "https://app/cb",
            Scopes: new[] { "openid" },
            AuthorizationDetailsJson: null
        );

        var (ok, _, _, _) = await svc.IssueAsync(valid, Guid.NewGuid());

        Assert.IsTrue(ok);
        var entity = await db.AuthorizationCodes.FirstOrDefaultAsync();
        Assert.IsNull(entity?.AuthorizationDetailsJson);
    }
}
