using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.WebAuth.Infrastructure.Startup;

public static class HttpsCertificateStartupValidator
{
    public static bool TryValidate(IConfiguration configuration, ILogger logger)
    {
        var certPath = GetConfiguredCertificatePath(configuration);
        if (string.IsNullOrWhiteSpace(certPath) || !RequiresHttpsEndpoint(configuration))
        {
            return true;
        }

        try
        {
            using var stream = File.OpenRead(certPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            LogMissingCertificate(logger, certPath);
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            LogMissingCertificate(logger, certPath);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogCritical(
                ex,
                "Configured HTTPS certificate file '{CertificatePath}' is not readable. Ensure the file is mounted and readable by the current process. For the published Docker setup, run 'chmod 644 ./certs/aspnetapp.pfx' and restart the stack.",
                certPath);
            return false;
        }
        catch (IOException ex)
        {
            logger.LogCritical(
                ex,
                "Configured HTTPS certificate file '{CertificatePath}' could not be opened during startup validation.",
                certPath);
            return false;
        }
    }

    public static bool RequiresHttpsEndpoint(IConfiguration configuration)
    {
        var urls = configuration["ASPNETCORE_URLS"]
            ?? configuration["urls"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS")
            ?? Environment.GetEnvironmentVariable("URLS");
        if (!string.IsNullOrWhiteSpace(urls) && urls.Contains("https://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var httpsPorts = configuration["HTTPS_PORTS"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_HTTPS_PORTS")
            ?? Environment.GetEnvironmentVariable("HTTPS_PORTS");
        if (!string.IsNullOrWhiteSpace(httpsPorts))
        {
            return true;
        }

        var endpoints = configuration.GetSection("Kestrel:Endpoints");
        foreach (var endpoint in endpoints.GetChildren())
        {
            var url = endpoint["Url"];
            if (!string.IsNullOrWhiteSpace(url) && url.Contains("https://", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetConfiguredCertificatePath(IConfiguration configuration)
    {
        return configuration["Kestrel:Certificates:Default:Path"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Path")
            ?? Environment.GetEnvironmentVariable("Kestrel__Certificates__Default__Path");
    }

    private static void LogMissingCertificate(ILogger logger, string certPath)
    {
        logger.LogCritical(
            "Configured HTTPS certificate file '{CertificatePath}' was not found. Startup is stopping before Kestrel binds HTTPS. For the published Docker setup, run 'bash ./scripts/generate-cert.sh localhost changeit', ensure CERT_PASSWORD matches the generated PFX, and mount './certs:/https:ro'.",
            certPath);
    }
}