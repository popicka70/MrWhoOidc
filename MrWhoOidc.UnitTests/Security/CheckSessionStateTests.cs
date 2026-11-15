using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// F1: the check_session iframe answers with the state of the actual session. A revoked, expired or idle session
/// answers "changed", so the relying party signs the user out even though the browser never came back to the OP.
/// </summary>
[TestClass]
public sealed class CheckSessionStateTests
{
    [TestMethod]
    public async Task ALiveSession_AnswersAlive()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Valid);

        var state = await CreateHandler(sessions.Object).GetStateAsync(CreateHttpContext(sessionId));

        Assert.AreEqual(CheckSessionState.Alive, state);
    }

    [TestMethod]
    [DataRow(OpSessionStatus.Revoked)]
    [DataRow(OpSessionStatus.Expired)]
    [DataRow(OpSessionStatus.IdleExpired)]
    [DataRow(OpSessionStatus.NotFound)]
    public async Task AnEndedSession_AnswersChanged(OpSessionStatus status)
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(status);

        var state = await CreateHandler(sessions.Object).GetStateAsync(CreateHttpContext(sessionId));

        Assert.AreEqual(CheckSessionState.Changed, state);
    }

    [TestMethod]
    public async Task AnUnknownCookie_AnswersError()
    {
        var sessions = new Mock<IOpSessionService>();

        var state = await CreateHandler(sessions.Object).GetStateAsync(CreateHttpContext(null));

        Assert.AreEqual(CheckSessionState.Error, state, "without the browser state cookie there is nothing to compare");
    }

    [TestMethod]
    public async Task ADatabaseOutage_DoesNotSignTheUserOut()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is gone"));

        var state = await CreateHandler(sessions.Object).GetStateAsync(CreateHttpContext(sessionId));

        Assert.AreEqual(CheckSessionState.Alive, state, "an outage must not make every relying party sign the user out");
    }

    [TestMethod]
    public async Task TheIframe_AsksTheServerForTheState()
    {
        var handler = new CheckSessionHandler();
        var http = new DefaultHttpContext();
        http.Request.Path = "/connect/checksession";
        http.Items["csp-nonce"] = "nonce-1";

        var result = await handler.HandleAsync(http);
        var html = await ReadHtmlAsync(result);

        StringAssert.Contains(html, "/connect/checksession/state");
        StringAssert.Contains(html, "XMLHttpRequest");
        StringAssert.Contains(html, "received_message");
        StringAssert.Contains(html, "postMessage");
        // The conformance suite drives this iframe with its own scripted browser (HtmlUnit), which has no
        // fetch() or WebCrypto: the comparison must happen server-side.
        Assert.IsFalse(html.Contains("fetch("), "the iframe must not use fetch()");
        Assert.IsFalse(html.Contains("crypto.subtle"), "the iframe must not use WebCrypto");
        Assert.IsFalse(html.Contains("async function"), "the iframe must not rely on ES2017");
    }

    [TestMethod]
    public async Task AValidMessageAndALiveSession_AnswerUnchanged()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Valid);

        var browserState = sessionId.ToString("N");
        var salt = "salty-value-1";
        var sessionState = SessionStateCalculator.Compute("client-1", "https://rp.example", browserState, salt);
        var message = $"client-1 {sessionState}";

        var state = await CreateHandler(sessions.Object)
            .CompareAsync(CreateHttpContext(sessionId), message, "https://rp.example");

        Assert.AreEqual(CheckSessionState.Unchanged, state);
    }

    [TestMethod]
    public async Task AMessageFromAnotherOrigin_AnswersChanged()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Valid);

        var browserState = sessionId.ToString("N");
        var salt = "salty-value-1";
        var sessionState = SessionStateCalculator.Compute("client-1", "https://rp.example", browserState, salt);
        var message = $"client-1 {sessionState}";

        var state = await CreateHandler(sessions.Object)
            .CompareAsync(CreateHttpContext(sessionId), message, "https://attacker.example");

        Assert.AreEqual(CheckSessionState.Changed, state, "a session_state must not verify against another origin");
    }

    [TestMethod]
    public async Task ARevokedSession_AnswersChanged_EvenWithAMatchingSessionState()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Revoked);

        var browserState = sessionId.ToString("N");
        var salt = "salty-value-1";
        var sessionState = SessionStateCalculator.Compute("client-1", "https://rp.example", browserState, salt);
        var message = $"client-1 {sessionState}";

        var state = await CreateHandler(sessions.Object)
            .CompareAsync(CreateHttpContext(sessionId), message, "https://rp.example");

        Assert.AreEqual(CheckSessionState.Changed, state, "the server-side session is the truth (F1)");
    }

    [TestMethod]
    public async Task AClientIdWithSpaces_Parses_WithTheSessionStateAfterTheLastSpace()
    {
        var sessionId = Guid.NewGuid();
        var sessions = new Mock<IOpSessionService>();
        sessions.Setup(s => s.GetStatusAsync(sessionId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OpSessionStatus.Valid);

        var browserState = sessionId.ToString("N");
        var clientId = "client with spaces";
        var salt = "salty-value-1";
        var sessionState = SessionStateCalculator.Compute(clientId, "https://rp.example", browserState, salt);
        var message = $"{clientId} {sessionState}";

        var state = await CreateHandler(sessions.Object)
            .CompareAsync(CreateHttpContext(sessionId), message, "https://rp.example");

        Assert.AreEqual(CheckSessionState.Unchanged, state);
    }

    [TestMethod]
    [DataRow("only-one-token")]
    [DataRow("")]
    [DataRow("  ")]
    [DataRow("client-1 hash-without-salt")]
    public async Task AMalformedMessage_AnswersError(string message)
    {
        var sessions = new Mock<IOpSessionService>();

        var state = await CreateHandler(sessions.Object)
            .CompareAsync(CreateHttpContext(Guid.NewGuid()), message, "https://rp.example");

        Assert.AreEqual(CheckSessionState.Error, state, "an unprocessable message must answer error");
    }

    [TestMethod]
    public async Task AMissingOrigin_AnswersError()
    {
        var sessionId = Guid.NewGuid();
        var browserState = sessionId.ToString("N");
        var sessionState = SessionStateCalculator.Compute("client-1", "https://rp.example", browserState, "salt");
        var message = $"client-1 {sessionState}";

        var state = await CreateHandler(new Mock<IOpSessionService>().Object)
            .CompareAsync(CreateHttpContext(sessionId), message, null);

        Assert.AreEqual(CheckSessionState.Error, state);
    }

    [TestMethod]
    public async Task ALegacyCookie_WithoutASessionId_StillComparesByHash()
    {
        var sessions = new Mock<IOpSessionService>();
        var browserState = "legacy-random-value";
        var salt = "salty-value-1";
        var sessionState = SessionStateCalculator.Compute("client-1", "https://rp.example", browserState, salt);
        var message = $"client-1 {sessionState}";

        var http = CreateHttpContext(null);
        http.Request.Headers["Cookie"] = $"{CheckSessionStateHandler.OpBrowserStateCookieName}={browserState}";

        var state = await CreateHandler(sessions.Object).CompareAsync(http, message, "https://rp.example");

        Assert.AreEqual(CheckSessionState.Unchanged, state);
        sessions.Verify(s => s.GetStatusAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static CheckSessionStateHandler CreateHandler(IOpSessionService sessions)
        => new(sessions, Microsoft.Extensions.Logging.Abstractions.NullLogger<CheckSessionStateHandler>.Instance);

    private static DefaultHttpContext CreateHttpContext(Guid? sessionId)
    {
        var http = new DefaultHttpContext();
        if (sessionId is { } id)
        {
            http.Request.Headers["Cookie"] = $"{CheckSessionStateHandler.OpBrowserStateCookieName}={id.ToString("N")}";
        }

        return http;
    }

    private static async Task<string> ReadHtmlAsync(IResult result)
    {
        Assert.IsInstanceOfType<Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult>(result);
        var content = (Microsoft.AspNetCore.Http.HttpResults.ContentHttpResult)result;
        return content.ResponseContent ?? string.Empty;
    }
}
