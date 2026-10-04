using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using MrWhoOidc.WebAuth.Handlers.External;

namespace MrWhoOidc.UnitTests.Security;

/// <summary>
/// External IdP state (2026-10-04 assessment): browser-bound, short-lived and single use.
/// End-to-end coverage (other browser / replay) lives in ExternalOidcIntegrationTests.
/// </summary>
[TestClass]
public sealed class ExternalStateBindingTests
{
    private static (ExternalOidcStateManager Manager, string State, string Binding) Issue()
    {
        var manager = new ExternalOidcStateManager(new EphemeralDataProtectionProvider());
        var start = new DefaultHttpContext();
        var state = manager.IssueBrowserBoundState(start, new StateModel { Provider = "up1", CodeVerifier = "v" });
        var setCookie = start.Response.Headers.SetCookie.ToString();
        var prefix = ExternalOidcStateManager.StateBindingCookie + "=";
        Assert.IsTrue(setCookie.StartsWith(prefix, StringComparison.Ordinal), setCookie);
        return (manager, state, setCookie[prefix.Length..setCookie.IndexOf(';')]);
    }

    private static HttpContext Callback(string? binding)
    {
        var ctx = new DefaultHttpContext();
        if (binding is not null) ctx.Request.Headers.Cookie = ExternalOidcStateManager.StateBindingCookie + "=" + binding;
        return ctx;
    }

    [TestMethod]
    public void State_WithBindingCookie_IsAccepted_AndCookieCleared()
    {
        var (manager, state, binding) = Issue();
        var ctx = Callback(binding);

        Assert.IsNotNull(manager.ConsumeBrowserBoundState(ctx, state));
        StringAssert.Contains(ctx.Response.Headers.SetCookie.ToString(), ExternalOidcStateManager.StateBindingCookie + "=;");
    }

    [TestMethod]
    public void State_WithoutOrWithWrongBindingCookie_IsRejected()
    {
        var (manager, state, _) = Issue();

        Assert.IsNull(manager.ConsumeBrowserBoundState(Callback(null), state));
        Assert.IsNull(manager.ConsumeBrowserBoundState(Callback("attacker-binding"), state));
    }

    [TestMethod]
    public void State_OlderThanLifetime_IsRejected()
    {
        var manager = new ExternalOidcStateManager(new EphemeralDataProtectionProvider());
        var state = manager.ProtectState(new StateModel
        {
            Provider = "up1",
            BrowserBindingHash = ExternalOidcStateManager.HashBinding("b"),
            IssuedAt = DateTimeOffset.UtcNow.Subtract(ExternalOidcStateManager.StateLifetime).AddMinutes(-1).ToUnixTimeSeconds()
        });

        Assert.IsNull(manager.ConsumeBrowserBoundState(Callback("b"), state));
    }

    [TestMethod]
    public void LegacyState_WithoutBinding_IsRejected()
    {
        var manager = new ExternalOidcStateManager(new EphemeralDataProtectionProvider());
        var state = manager.ProtectState(new StateModel { Provider = "up1", IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        Assert.IsNull(manager.ConsumeBrowserBoundState(Callback("b"), state));
    }
}
