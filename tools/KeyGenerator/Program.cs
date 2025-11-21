using System.Security.Cryptography;

var pemPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "secrets", "licensing-private-key.pem");
Directory.CreateDirectory(Path.GetDirectoryName(pemPath)!);

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
    File.WriteAllText(pemPath, ecdsa.ExportECPrivateKeyPem());
    Console.WriteLine($"✅ Generated new licensing-private-key.pem at {pemPath}");
}

var publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
Console.WriteLine("\n--- PUBLIC KEY (Copy to EmbeddedLicensingKeys.cs) ---");
Console.WriteLine(publicKeyPem);
Console.WriteLine("-----------------------------------------------------");
