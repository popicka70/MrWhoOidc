using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

public interface IQrCodeGenerator
{
    string GenerateQrCodeDataUri(string url);
}

public sealed class QrCodeGenerator : IQrCodeGenerator
{
    public QrCodeGenerator(IOptions<QrLoginOptions> options)
    {
    }

    public string GenerateQrCodeDataUri(string url)
    {
        return url;
    }
}
