using System;
using System.IO;
using System.Security.Cryptography;

class Program
{
    static void Main()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = ecdsa.ExportECPrivateKeyPem();
        File.WriteAllText("secrets/licensing-private-key.pem", privateKey);
        Console.WriteLine("✅ Generated licensing-private-key.pem");
    }
}
