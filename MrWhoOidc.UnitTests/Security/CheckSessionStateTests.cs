using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Handlers;

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
        StringAssert.Contains(html, "readSessionState");
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
