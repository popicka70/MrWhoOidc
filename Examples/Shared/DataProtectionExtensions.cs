using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MrWhoOidc.Examples;

public static class DataProtectionExtensions
{
    public static IServiceCollection AddExampleDataProtection(this IServiceCollection services, IConfiguration configuration)
    {
        var directory = configuration["DataProtection:KeysDirectory"];
        if (string.IsNullOrWhiteSpace(directory))
        {
            return services;
        }

        var certificatePath = configuration["DataProtection:CertificatePath"];
        if (string.IsNullOrWhiteSpace(certificatePath))
        {
            throw new InvalidOperationException("DataProtection:CertificatePath is required when DataProtection:KeysDirectory is configured.");
        }

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            certificatePath, configuration["DataProtection:CertificatePassword"]);
        if (!certificate.HasPrivateKey)
        {
            throw new InvalidOperationException("The DataProtection certificate must contain a private key.");
        }

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(directory))
            .ProtectKeysWithCertificate(certificate);
        return services;
    }
}
