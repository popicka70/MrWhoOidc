using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Manages state protection and unprotection for external OIDC flows.
/// </summary>
public interface IExternalOidcStateManager
{
    string ProtectState(StateModel model);
    StateModel? UnprotectState(string protectedState);

    /// <summary>
    /// Binds <paramref name="model"/> to the current browser (HttpOnly nonce cookie whose hash is stored in
    /// the state), stamps its issue time and returns the protected state.
    /// </summary>
    string IssueBrowserBoundState(HttpContext http, StateModel model);

    /// <summary>
    /// Unprotects the state and accepts it only when it is unexpired and bound to this browser; on success the
    /// binding cookie is cleared so the state cannot be used again. Returns null when the state is rejected.
    /// </summary>
    StateModel? ConsumeBrowserBoundState(HttpContext http, string protectedState);
    string ProtectConfirm(ConfirmModel model);
    ConfirmModel? UnprotectConfirm(string protectedConfirm);
}

internal sealed class ExternalOidcStateManager : IExternalOidcStateManager
{
    internal const string StateBindingCookie = "__Host-mrwho-ext-state";
    internal static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly IDataProtector _stateProtector;
    private readonly IDataProtector _confirmProtector;

    public ExternalOidcStateManager(IDataProtectionProvider dp)
    {
        _stateProtector = dp.CreateProtector("ext-oidc-state");
        _confirmProtector = dp.CreateProtector("ext-oidc-confirm");
    }

    public string ProtectState(StateModel model)
    {
        var json = JsonSerializer.Serialize(model);
        var bytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = _stateProtector.Protect(bytes);
        return ExternalOidcEncodingHelpers.Base64UrlEncode(protectedBytes);
    }

    public StateModel? UnprotectState(string protectedState)
    {
        try
        {
            var bytes = ExternalOidcEncodingHelpers.Base64UrlDecode(protectedState);
            var unprotected = _stateProtector.Unprotect(bytes);
            return JsonSerializer.Deserialize<StateModel>(unprotected);
        }
        catch
        {
            return null;
        }
    }

    public string IssueBrowserBoundState(HttpContext http, StateModel model)
    {
        var binding = ExternalOidcEncodingHelpers.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        model.BrowserBindingHash = HashBinding(binding);
        model.IssuedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        http.Response.Cookies.Append(StateBindingCookie, binding, BindingCookieOptions(StateLifetime));
        return ProtectState(model);
    }

    public StateModel? ConsumeBrowserBoundState(HttpContext http, string protectedState)
    {
        var model = UnprotectState(protectedState);
        if (model is null || string.IsNullOrEmpty(model.BrowserBindingHash))
            return null;

        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(model.IssuedAt);
        var now = DateTimeOffset.UtcNow;
        if (issuedAt > now.Add(ClockSkew) || now - issuedAt > StateLifetime)
            return null;

        if (!http.Request.Cookies.TryGetValue(StateBindingCookie, out var binding) || string.IsNullOrEmpty(binding)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(HashBinding(binding)), Encoding.ASCII.GetBytes(model.BrowserBindingHash)))
            return null;

        // Single use: the binding cookie is consumed with the state.
        http.Response.Cookies.Delete(StateBindingCookie, BindingCookieOptions(null));
        return model;
    }

    internal static string HashBinding(string binding)
        => ExternalOidcEncodingHelpers.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));

    private static CookieOptions BindingCookieOptions(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge
    };

    public string ProtectConfirm(ConfirmModel model)
    {
        var json = JsonSerializer.Serialize(model);
        var bytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = _confirmProtector.Protect(bytes);
        return ExternalOidcEncodingHelpers.Base64UrlEncode(protectedBytes);
    }

    public ConfirmModel? UnprotectConfirm(string protectedConfirm)
    {
        try
        {
            var bytes = ExternalOidcEncodingHelpers.Base64UrlDecode(protectedConfirm);
            var unprotected = _confirmProtector.Unprotect(bytes);
            return JsonSerializer.Deserialize<ConfirmModel>(unprotected);
        }
        catch
        {
            return null;
        }
    }
}
