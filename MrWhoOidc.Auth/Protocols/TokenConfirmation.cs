using System.Text.Json;

namespace MrWhoOidc.Auth.Protocols;

public readonly record struct TokenConfirmation(string? Jkt, string? X5tS256)
{
    public static bool TryParse(object? value, out TokenConfirmation confirmation)
    {
        confirmation = default;

        string json;
        try
        {
            json = value is string raw ? raw : JsonSerializer.Serialize(value);
        }
        catch (NotSupportedException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string? jkt = null;
            string? x5tS256 = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var thumbprint = property.Value.GetString();
                if (string.IsNullOrWhiteSpace(thumbprint))
                {
                    return false;
                }

                switch (property.Name)
                {
                    case "jkt" when jkt is null:
                        jkt = thumbprint;
                        break;
                    case "x5t#S256" when x5tS256 is null:
                        x5tS256 = thumbprint;
                        break;
                    default:
                        return false;
                }
            }

            if (jkt is null && x5tS256 is null)
            {
                return false;
            }

            confirmation = new TokenConfirmation(jkt, x5tS256);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
