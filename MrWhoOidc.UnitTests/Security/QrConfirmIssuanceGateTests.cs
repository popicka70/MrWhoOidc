using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Moq;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.Auth.Services.Authorization;
using MrWhoOidc.UnitTests.Helpers;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Observability;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// V5 of the third 2026-10-04 review: the QR branch of /authorize returned before the user-client assignment and
/// consent checks, so a user not assigned to a restricted client still got a code for it via ?qr=1.
/// </summary>
[TestClass]
public sealed class QrConfirmIssuanceGateTests
{
    private const string SessionToken = "qr-session-token";
    private static readonly Guid UserId = Guid.NewGuid();

    private static (QrLoginHandler Handler, Mock<IAuthorizationCodeService> Codes, Mock<IQrLoginService> Qr) CreateHandler(
        bool assigned, bool consentMissing, UserStatus status = UserStatus.Active, string returnUrl = "https://rp.example/callback")
    {
        var qr = new Mock<IQrLoginService>();
        qr.Setup(q => q.GetSessionAsync(SessionToken)).ReturnsAsync(new QrLoginSession
        {
            SessionToken = SessionToken,
            ClientId = "restricted-app",
            ReturnUrl = returnUrl,
            CodeChallenge = "challenge",
            Scope = "openid profile",
            Status = QrSessionStatus.Scanned,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(2),
        });

        var assignments = new Mock<IUserClientAssignmentService>();
        assignments.Setup(a => a.EnsureAssignedAsync(It.IsAny<Guid>(), "restricted-app", It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(assigned ? (true, null) : (false, "User is not assigned to this application"));
        var consent = new Mock<IConsentProcessor>();
        consent.Setup(c => c.EvaluateAsync(It.IsAny<Guid>(), "restricted-app", It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsentDecision(RequiresConsent: consentMissing, HasConsent: !consentMissing));

        var codes = new Mock<IAuthorizationCodeService>();
        var db = TestDataSeeder.CreateInMemoryDb();
        db.Users.Add(new User { Id = UserId, TenantId = Guid.NewGuid(), Username = "alice", Status = status });
        db.SaveChanges();
        var handler = new QrLoginHandler(
            qr.Object,
            Mock.Of<IQrCodeGenerator>(),
            codes.Object,
            db,
            NullLogger<QrLoginHandler>.Instance,
            new NoopAuditSink(),
            Options.Create(new QrLoginOptions()),
            Mock.Of<ILoginContinuationStore>(),
            assignments.Object,
            consent.Object);
        return (handler, codes, qr);
    }

    private static DefaultHttpContext ConfirmRequest()
    {
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(new Dictionary<string, StringValues> { ["sessionToken"] = SessionToken });
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId.ToString())], "Cookies"));
        return http;
    }

    [TestMethod]
    [DataRow(false, false, DisplayName = "not assigned")]
    [DataRow(true, true, DisplayName = "assigned, consent missing")]
    public async Task Confirm_WhenUserMayNotUseTheClient_IssuesNoCode(bool assigned, bool consentMissing)
    {
        var (handler, codes, _) = CreateHandler(assigned, consentMissing);

        var result = await handler.ConfirmAsync(ConfirmRequest());

        Assert.AreEqual(403, (result as IStatusCodeHttpResult)?.StatusCode);
        codes.Verify(c => c.IssueAsync(It.IsAny<MrWhoOidc.Auth.Services.Authorization.AuthorizeValidationResult>(), It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>
    /// A deactivated user's mobile session must not approve a desktop login: neither the OAuth branch (code) nor
    /// the platform branch (session marked authenticated).
    /// </summary>
    [TestMethod]
    [DataRow("https://rp.example/callback", DisplayName = "oauth")]
    [DataRow("/Account", DisplayName = "platform")]
    public async Task Confirm_WhenUserIsDeactivated_IsRejected(string returnUrl)
    {
        var (handler, codes, qr) = CreateHandler(assigned: true, consentMissing: false, UserStatus.Deactivated, returnUrl);

        var result = await handler.ConfirmAsync(ConfirmRequest());

        Assert.AreEqual(403, (result as IStatusCodeHttpResult)?.StatusCode);
        codes.Verify(c => c.IssueAsync(It.IsAny<MrWhoOidc.Auth.Services.Authorization.AuthorizeValidationResult>(), It.IsAny<Guid>()), Times.Never);
        qr.Verify(q => q.UpdateStatusAsync(It.IsAny<string>(), QrSessionStatus.Authenticated, It.IsAny<Guid?>(), It.IsAny<string?>()), Times.Never);
    }
}
