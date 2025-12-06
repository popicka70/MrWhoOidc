namespace LicensingService.Core;

/// <summary>
/// Helper class for generating UUIDv7 identifiers.
/// Per constitution: Entity primary keys must use GuidHelper.NewId() (not Guid.NewGuid()).
/// </summary>
public static class GuidHelper
{
    /// <summary>
    /// Generates a new UUIDv7 identifier.
    /// UUIDv7 is time-ordered, which provides better database index performance.
    /// </summary>
    public static Guid NewId()
    {
        // UUIDv7 format:
        // - 48 bits: Unix timestamp in milliseconds
        // - 4 bits: Version (7)
        // - 12 bits: Random
        // - 2 bits: Variant (10)
        // - 62 bits: Random

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> bytes = stackalloc byte[16];

        // Fill with random bytes first
        Random.Shared.NextBytes(bytes);

        // Set timestamp (48 bits = 6 bytes) in big-endian order
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;

        // Set version (4 bits = 0111 = 7) in byte 6, high nibble
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);

        // Set variant (2 bits = 10) in byte 8, high bits
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }
}
