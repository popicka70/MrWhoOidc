using System.Security.Cryptography;

using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var pemPath = Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "secrets", "licensing-private-key.pem");
Directory.CreateDirectory(Path.GetDirectoryName(pemPath)!);
File.WriteAllText(pemPath, ecdsa.ExportECPrivateKeyPem());
Console.WriteLine($"✅ Generated licensing-private-key.pem at {pemPath}");
