using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using QRCoder;

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
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("QR code payload must not be empty.", nameof(url));
        }

        using var qrGenerator = new QRCodeGenerator();
        using var qrData = qrGenerator.CreateQrCode(url, ResolveErrorCorrectionLevel(_options.QrCodeErrorCorrectionLevel));
        var qrCode = new PngByteQRCode(qrData);
        var pixelsPerModule = _options.QrCodePixelsPerModule > 0 ? _options.QrCodePixelsPerModule : 10;
        var bytes = qrCode.GetGraphic(pixelsPerModule);
        return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
    }

    private static QRCodeGenerator.ECCLevel ResolveErrorCorrectionLevel(string? value)
    {
        return value?.Trim().ToUpperInvariant() switch
        {
            "L" or "LOW" => QRCodeGenerator.ECCLevel.L,
            "Q" or "QUARTILE" => QRCodeGenerator.ECCLevel.Q,
            "H" or "HIGH" => QRCodeGenerator.ECCLevel.H,
            _ => QRCodeGenerator.ECCLevel.M
        };
    }
}
