using System.ComponentModel.DataAnnotations;
using System.Net.Mail;

namespace MrWhoOidc.Auth.Persistence;

public static class EmailNormalizer
{
    private static readonly EmailAddressAttribute Validator = new();

    public static string? NormalizeForLookup(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.Trim().ToLowerInvariant();
    }

    public static string? FormatForStorage(string? value, bool required, out string? normalized)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                throw new ValidationException("Email is required.");
            }

            normalized = null;
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            if (required)
            {
                throw new ValidationException("Email is required.");
            }

            normalized = null;
            return null;
        }

        if (!IsFormatValid(trimmed))
        {
            throw new ValidationException($"Invalid email address '{trimmed}'.");
        }

        normalized = trimmed.ToLowerInvariant();
        return trimmed;
    }

    private static bool IsFormatValid(string value)
    {
        if (!Validator.IsValid(value))
        {
            return false;
        }

        try
        {
            _ = new MailAddress(value);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
