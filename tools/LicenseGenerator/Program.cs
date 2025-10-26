using System;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LicenseGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0 || HasHelpFlag(args))
        {
            PrintUsage();
            return 0;
        }

        if (!LicenseGeneratorOptionsParser.TryParse(args, out var options, out var errorMessage))
        {
            Console.Error.WriteLine(errorMessage);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        try
        {
            var token = GenerateLicenseToken(options);

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                File.WriteAllText(options.OutputPath!, token, Encoding.ASCII);
                Console.WriteLine($"License token written to {options.OutputPath}");
            }

            Console.WriteLine(token);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to generate license token: {ex.Message}");
            return 2;
        }
    }

    private static bool HasHelpFlag(IEnumerable<string> args)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)
                || string.Equals(arg, "-?", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GenerateLicenseToken(LicenseGeneratorOptions options)
    {
    var now = DateTimeOffset.UtcNow;
    var issuedAt = options.ValidFrom <= now ? options.ValidFrom : now;

        using var ecdsa = LoadPrivateKey(options.PrivateKeyPath);
        var securityKey = new ECDsaSecurityKey(ecdsa)
        {
            KeyId = string.IsNullOrWhiteSpace(options.KeyId) ? LicenseDefaults.KeyId : options.KeyId
        };

        var signingCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.EcdsaSha256);

        var payload = new JwtPayload
        {
            { JwtRegisteredClaimNames.Iss, string.IsNullOrWhiteSpace(options.Issuer) ? LicenseDefaults.Issuer : options.Issuer },
            { JwtRegisteredClaimNames.Nbf, EpochTime.GetIntDate(options.ValidFrom.UtcDateTime) },
            { JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(issuedAt.UtcDateTime) },
            { JwtRegisteredClaimNames.Exp, EpochTime.GetIntDate(options.ValidUntil.UtcDateTime) },
            { JwtRegisteredClaimNames.Jti, string.IsNullOrWhiteSpace(options.TokenId) ? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : options.TokenId }
        };

        payload["tier"] = options.Tier;

        if (!string.IsNullOrWhiteSpace(options.Organization))
        {
            payload["organization"] = options.Organization;
        }

        if (options.Features.Count > 0)
        {
            payload["features"] = options.Features.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (options.Limits.Count > 0)
        {
            payload["limits"] = options.Limits;
        }

        var header = new JwtHeader(signingCredentials);
        var token = new JwtSecurityToken(header, payload);
        var handler = new JwtSecurityTokenHandler
        {
            // Preserve raw claim names for compatibility with server-side validator.
            MapInboundClaims = false
        };

        return handler.WriteToken(token);
    }

    private static ECDsa LoadPrivateKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Private key path is required.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Private key file not found.", path);
        }

        var pem = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(pem))
        {
            throw new InvalidOperationException("Private key file is empty.");
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(pem);
            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static void PrintUsage()
    {
        const string usage = """
LicenseGenerator - Generate MrWhoOidc license tokens

Usage:
  dotnet run --project tools/LicenseGenerator -- \
    --tier <tier> \
    --private-key <path-to-pem> \
    [--organization <name>] \
    [--valid-from <ISO-8601>] \
    [--valid-until <ISO-8601>] \
    [--valid-days <days>] \
    [--feature <feature>]... \
    [--limit <name=value>]... \
    [--issuer <issuer>] \
    [--key-id <kid>] \
    [--token-id <id>] \
    [--output <file>]

Examples:
  dotnet run --project tools/LicenseGenerator -- \
    --tier enterprise --organization "Contoso" \
    --valid-days 365 --feature analytics --feature dpop \
    --limit tenants=50 --limit users=1000 \
    --private-key .\secrets\licensing-private.pem \
    --output .\contoso-license.jwt
""";

        Console.WriteLine(usage);
    }
}

internal static class LicenseDefaults
{
    public const string Issuer = "MrWhoOidc-License-Authority";

    public const string KeyId = "licensing-private-key";
}

internal sealed class LicenseGeneratorOptions
{
    public string Tier { get; set; } = string.Empty;

    public string? Organization { get; set; }

    public DateTimeOffset ValidFrom { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ValidUntil { get; set; } = DateTimeOffset.UtcNow.AddYears(1);

    public List<string> Features { get; } = new();

    public Dictionary<string, long> Limits { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string PrivateKeyPath { get; set; } = string.Empty;

    public string Issuer { get; set; } = LicenseDefaults.Issuer;

    public string KeyId { get; set; } = LicenseDefaults.KeyId;

    public string TokenId { get; set; } = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    public string? OutputPath { get; set; }
}

internal static class LicenseGeneratorOptionsParser
{
    public static bool TryParse(string[] args, out LicenseGeneratorOptions options, out string error)
    {
        options = new LicenseGeneratorOptions();
        error = string.Empty;

        int? validDays = null;
        var explicitValidUntil = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--tier":
                case "-t":
                    options.Tier = ReadNext(args, ref i, arg);
                    break;
                case "--organization":
                case "-o":
                    options.Organization = ReadNext(args, ref i, arg);
                    break;
                case "--valid-from":
                    if (!TryParseDate(ReadNext(args, ref i, arg), out var validFrom, out error))
                    {
                        return false;
                    }

                    options.ValidFrom = validFrom;
                    break;
                case "--valid-until":
                    if (!TryParseDate(ReadNext(args, ref i, arg), out var validUntil, out error))
                    {
                        return false;
                    }

                    options.ValidUntil = validUntil;
                    explicitValidUntil = true;
                    break;
                case "--valid-days":
                    if (!int.TryParse(ReadNext(args, ref i, arg), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
                    {
                        error = "--valid-days must be a positive integer.";
                        return false;
                    }

                    validDays = days;
                    break;
                case "--feature":
                    var featureValue = ReadNext(args, ref i, arg);
                    foreach (var feature in Split(featureValue))
                    {
                        if (!string.IsNullOrWhiteSpace(feature))
                        {
                            options.Features.Add(feature.Trim());
                        }
                    }

                    break;
                case "--limit":
                    var limitValue = ReadNext(args, ref i, arg);
                    if (!TryParseLimit(limitValue, out var name, out var limit, out error))
                    {
                        return false;
                    }

                    options.Limits[name] = limit;
                    break;
                case "--private-key":
                case "-k":
                    options.PrivateKeyPath = ReadNext(args, ref i, arg);
                    break;
                case "--issuer":
                    options.Issuer = ReadNext(args, ref i, arg);
                    break;
                case "--key-id":
                    options.KeyId = ReadNext(args, ref i, arg);
                    break;
                case "--token-id":
                    options.TokenId = ReadNext(args, ref i, arg);
                    break;
                case "--output":
                case "-f":
                    options.OutputPath = ReadNext(args, ref i, arg);
                    break;
                default:
                    error = $"Unknown argument '{arg}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(options.Tier))
        {
            error = "--tier is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            error = "--private-key is required.";
            return false;
        }

        if (!explicitValidUntil && validDays.HasValue)
        {
            options.ValidUntil = options.ValidFrom.AddDays(validDays.Value);
        }

        if (options.ValidUntil <= options.ValidFrom)
        {
            error = "valid-until must be greater than valid-from.";
            return false;
        }

        return true;
    }

    private static string ReadNext(string[] args, ref int index, string current)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {current}.");
        }

        index += 1;
        return args[index];
    }

    private static bool TryParseDate(string input, out DateTimeOffset value, out string error)
    {
        if (DateTimeOffset.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value))
        {
            error = string.Empty;
            return true;
        }

        error = $"Unable to parse date '{input}'. Use ISO-8601 (e.g., 2025-01-01T00:00:00Z).";
        return false;
    }

    private static bool TryParseLimit(string input, out string name, out long value, out string error)
    {
        error = string.Empty;
        name = string.Empty;
        value = default;

        var parts = input.Split('=', 2);
        if (parts.Length != 2)
        {
            error = "Limits must use the form name=value (e.g., users=1000 or tenants=-1).";
            return false;
        }

        name = parts[0].Trim();
        var valuePart = parts[1].Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Limit name cannot be empty.";
            return false;
        }

        if (!long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            error = $"Limit value '{valuePart}' is not a valid integer.";
            return false;
        }

        return true;
    }

    private static IEnumerable<string> Split(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
