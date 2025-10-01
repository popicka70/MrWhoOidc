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
    string ProtectConfirm(ConfirmModel model);
    ConfirmModel? UnprotectConfirm(string protectedConfirm);
}

internal sealed class ExternalOidcStateManager : IExternalOidcStateManager
{
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
