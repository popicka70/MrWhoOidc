using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Data.Common;

namespace MrWhoOidc.WebAuth.Infrastructure.Startup;

public static class HttpsCertificateStartupValidator
{
    public static bool TryValidate(IConfiguration configuration, ILogger logger)
    {
        if (IsProductionLike(configuration) && !ValidateProductionSecrets(configuration, logger))
        {
            return false;
        }

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

    private static string? GetConfiguredCertificatePassword(IConfiguration configuration)
    {
        return configuration["Kestrel:Certificates:Default:Password"]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_Kestrel__Certificates__Default__Password")
            ?? Environment.GetEnvironmentVariable("Kestrel__Certificates__Default__Password")
            ?? Environment.GetEnvironmentVariable("CERT_PASSWORD");
    }

    private static bool ValidateProductionSecrets(IConfiguration configuration, ILogger logger)
    {
        var valid = true;

        if (!string.IsNullOrWhiteSpace(GetConfiguredCertificatePath(configuration)) &&
            IsWeakSecret(GetConfiguredCertificatePassword(configuration)))
        {
            logger.LogCritical("Production HTTPS certificate password is missing or uses a weak/default value. Set Kestrel:Certificates:Default:Password from a deployment secret before starting.");
            valid = false;
        }

        if (TryGetConfiguredDatabasePassword(configuration, out var databasePassword) && IsWeakSecret(databasePassword))
        {
            logger.LogCritical("Production auth database password is missing or uses a weak/default value. Rotate POSTGRES_PASSWORD/ConnectionStrings:authdb before starting.");
            valid = false;
        }

        var bootstrapToken = configuration["Bootstrap:Token"] ?? Environment.GetEnvironmentVariable("BOOTSTRAP_TOKEN");
        if (!string.IsNullOrWhiteSpace(bootstrapToken) && IsWeakSecret(bootstrapToken))
        {
            logger.LogCritical("Production bootstrap token uses a weak/default value. Generate a high-entropy one-time token before starting.");
            valid = false;
        }

        return valid;
    }

    private static bool TryGetConfiguredDatabasePassword(IConfiguration configuration, out string? password)
    {
        password = null;
        var connectionString = configuration.GetConnectionString("authdb")
            ?? configuration.GetConnectionString("AuthDb")
            ?? configuration["ConnectionStrings:authdb"]
            ?? Environment.GetEnvironmentVariable("AUTHDB__CONNECTIONSTRING")
            ?? Environment.GetEnvironmentVariable("AUTHDB_CONNECTIONSTRING");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return false;
        }

        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in new[] { "Password", "Pwd" })
            {
                if (builder.TryGetValue(key, out var value))
                {
                    password = value?.ToString();
                    return true;
                }
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }

    private static bool IsProductionLike(IConfiguration configuration)
    {
        var environmentName = configuration["ASPNETCORE_ENVIRONMENT"]
            ?? configuration["DOTNET_ENVIRONMENT"]
            ?? configuration[WebHostDefaults.EnvironmentKey]
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";

        return !string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(environmentName, "Testing", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeakSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16)
        {
            return true;
        }

        var normalized = value.Trim().ToLowerInvariant();
        string[] weakMarkers = ["changeit", "changeme", "password", "default", "example", "secret", "todo"];
        return weakMarkers.Any(normalized.Contains);
    }

    private static void LogMissingCertificate(ILogger logger, string certPath)
    {
        logger.LogCritical(
            "Configured HTTPS certificate file '{CertificatePath}' was not found. Startup is stopping before Kestrel binds HTTPS. For the published Docker setup, run 'bash ./scripts/generate-cert.sh localhost <strong-local-password>', ensure CERT_PASSWORD matches the generated PFX, and mount './certs:/https:ro'.",
            certPath);
    }
}