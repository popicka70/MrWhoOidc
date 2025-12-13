using System.Security.Cryptography;
using System.Text;

namespace MrWhoOidc.WebAuth.Infrastructure.Logging;

internal static class LogTokenization
{
    public static string HashId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "(null)";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).Substring(0, 12);
    }
}
