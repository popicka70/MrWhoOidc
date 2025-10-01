using System.Text;

namespace MrWhoOidc.WebAuth.Handlers.External;

/// <summary>
/// Extension methods for Base64Url encoding and decoding.
/// </summary>
internal static class ExternalOidcEncodingHelpers
{
    public static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static string Base64UrlDecodeToString(string s)
        => Encoding.UTF8.GetString(Base64UrlDecode(s));
}
