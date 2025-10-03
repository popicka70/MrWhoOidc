using QRCoder;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;

namespace MrWhoOidc.WebAuth.Services;

public interface IQrCodeGenerator
{
    string GenerateQrCodeDataUri(string url);
}

public sealed class QrCodeGenerator : IQrCodeGenerator
{
    private readonly QrLoginOptions _options;

    public QrCodeGenerator(IOptions<QrLoginOptions> options)
    {
        _options = options.Value;
    }

    public string GenerateQrCodeDataUri(string url)
    {
        using var qrGenerator = new QRCodeGenerator();
        
        // Parse error correction level
        var eccLevel = _options.QrCodeErrorCorrectionLevel.ToUpperInvariant() switch
        {
            "L" => QRCodeGenerator.ECCLevel.L,
            "M" => QRCodeGenerator.ECCLevel.M,
            "Q" => QRCodeGenerator.ECCLevel.Q,
            "H" => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.M
        };

        using var qrCodeData = qrGenerator.CreateQrCode(url, eccLevel);
        using var qrCode = new PngByteQRCode(qrCodeData);
        
        var qrCodeBytes = qrCode.GetGraphic(_options.QrCodePixelsPerModule);
        var base64 = Convert.ToBase64String(qrCodeBytes);
        
        return $"data:image/png;base64,{base64}";
    }
}
