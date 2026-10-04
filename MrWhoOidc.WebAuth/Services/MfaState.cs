using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

/// <summary>
/// TOTP is enrolled on the global <see cref="UserAccount"/> (/Mfa, /LoginTotp). The per-tenant
/// <see cref="User.TotpEnabled"/> flag is a legacy copy that stays false in every tenant the user joined later, so
/// passkey, external-IdP, device and CIBA sign-ins that read it skipped the second factor (H8).
/// </summary>
internal static class MfaState
{
    public static async Task<bool> HasTotpAsync(HttpContext http, User user)
    {
        var accounts = http.RequestServices.GetService<IUserAccountService>();
        var account = accounts is null ? null : await accounts.FindForUserAsync(user, http.RequestAborted);
        return account is not null
            ? account.TotpEnabled && !string.IsNullOrEmpty(account.TotpSecret)
            : user.TotpEnabled;
    }
}
