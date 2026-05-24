using System.Text;
using Microsoft.Extensions.Options;
using MrWhoOidc.Auth.Services;
using MrWhoOidc.WebAuth.Services;

namespace MrWhoOidc.UnitTests;

[TestClass]
public sealed class QrCodeGeneratorTests
{
    [TestMethod]
    public void GenerateQrCodeDataUri_ReturnsPngImageDataUri()
    {
        var generator = new QrCodeGenerator(Options.Create(new QrLoginOptions
        {
            QrCodePixelsPerModule = 4,
            QrCodeErrorCorrectionLevel = "M"
        }));

        var result = generator.GenerateQrCodeDataUri("otpauth://totp/MrWho:test@example.com?secret=ABCDEF&issuer=MrWho");

        Assert.IsTrue(result.StartsWith("data:image/png;base64,", StringComparison.Ordinal), result);
        var payload = result["data:image/png;base64,".Length..];
        var bytes = Convert.FromBase64String(payload);
        var pngHeader = Encoding.ASCII.GetString(bytes, 1, 3);
        Assert.AreEqual("PNG", pngHeader);
        Assert.IsTrue(bytes.Length > 100, "QR code image payload is unexpectedly small.");
    }
}