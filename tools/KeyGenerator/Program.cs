using System.Security.Cryptography;

var pemPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "secrets", "licensing-private-key.pem");
var secretsDir = Path.GetDirectoryName(pemPath)!;
if (OperatingSystem.IsWindows())
{
    Directory.CreateDirectory(secretsDir);
}
else
{
    // 0700 when created; an existing directory keeps its mode.
    Directory.CreateDirectory(secretsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}

using var ecdsa = ECDsa.Create();
if (File.Exists(pemPath))
{
    var pemContent = File.ReadAllText(pemPath);
    ecdsa.ImportFromPem(pemContent);
    Console.WriteLine($"Loaded existing private key from {pemPath}");
}
else
{
    ecdsa.GenerateKey(ECCurve.NamedCurves.nistP256);
    WriteOwnerOnly(pemPath, ecdsa.ExportECPrivateKeyPem());
    Console.WriteLine($"✅ Generated new licensing-private-key.pem at {pemPath}");
}

var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
Console.WriteLine("\n--- PUBLIC KEY (Copy to EmbeddedLicensingKeys.cs) ---");
Console.WriteLine(publicKeyPem);
Console.WriteLine("-----------------------------------------------------");

// Creates the private key file as 0600 on Unix from the start (no umask-default window).
// On Windows the file inherits the directory ACL; DPAPI protection is future work.
static void WriteOwnerOnly(string path, string content)
{
    var options = new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        Share = FileShare.None
    };
    if (!OperatingSystem.IsWindows())
    {
        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    }

    using var stream = new FileStream(path, options);
    using var writer = new StreamWriter(stream);
    writer.Write(content);
}
