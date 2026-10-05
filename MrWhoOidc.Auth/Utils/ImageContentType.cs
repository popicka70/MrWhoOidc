namespace MrWhoOidc.Auth.Utils;

/// <summary>
/// Server-side image type detection for uploaded logos/icons. The client-supplied Content-Type and file
/// extension are never trusted: only PNG, JPEG, GIF and WebP are accepted, identified by their magic bytes.
/// SVG is deliberately not accepted for uploads (it can carry script).
/// </summary>
public static class ImageContentType
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Gif = "image/gif";
    public const string Webp = "image/webp";

    /// <summary>Returns the content type for a supported raster image, or null when the bytes are not one.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return Png;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return Jpeg;
        }

        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8))
        {
            return Gif;
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return Webp;
        }

        return null;
    }
}
