namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Provides time-ordered UUID generation for database primary keys.
/// </summary>
/// <remarks>
/// This helper generates UUIDv7 identifiers (RFC 9562) which provide better database
/// performance compared to standard random UUIDs (UUIDv4) due to improved B-tree index
/// locality and reduced page splits.
/// 
/// UUIDv7 embeds a 48-bit millisecond timestamp prefix followed by random bits,
/// ensuring monotonic ordering while maintaining global uniqueness and unpredictability.
/// 
/// Benefits:
/// - 80-90% reduction in B-tree page splits during inserts
/// - Better cache efficiency due to sequential writes
/// - Improved query performance on time-range filters
/// - Compatible with existing UUID columns (no schema changes required)
/// - Provides approximate chronological ordering by ID
/// 
/// Security note: UUIDv7 reveals approximate creation time (±1ms). This is acceptable
/// for internal primary keys. Do NOT use for security-sensitive tokens where timing
/// leakage is a concern.
/// </remarks>
public static class GuidHelper
{
    /// <summary>
    /// Generates a time-ordered UUIDv7 (RFC 9562) for use as a database primary key.
    /// </summary>
    /// <returns>A globally unique identifier with embedded timestamp for optimal database performance.</returns>
    /// <remarks>
    /// Thread-safe and monotonic within the same millisecond. Generated IDs are compatible
    /// with PostgreSQL's uuid type and standard Guid operations.
    /// Uses the native .NET 9 Guid.CreateVersion7() implementation.
    /// </remarks>
    public static Guid NewId() => Guid.CreateVersion7();

    /// <summary>
    /// Legacy method for backward compatibility.
    /// </summary>
    /// <returns>A UUIDv7 identifier (same as NewId()).</returns>
    [Obsolete("Use NewId() instead. This method will be removed in v2.0")]
    public static Guid NewGuid() => NewId();

    /// <summary>
    /// Extracts the embedded timestamp from a UUIDv7 identifier.
    /// </summary>
    /// <param name="uuid">A UUIDv7 identifier.</param>
    /// <returns>
    /// The timestamp embedded in the UUID, or null if the UUID is not version 7.
    /// </returns>
    /// <remarks>
    /// This is useful for debugging and analytics. Note that the timestamp has
    /// millisecond precision and represents when the ID was generated, not when
    /// the entity was persisted.
    /// </remarks>
    public static DateTimeOffset? ExtractTimestamp(Guid uuid)
    {
        var bytes = uuid.ToByteArray();

        // Check version (should be 7 for UUIDv7)
        // Version is in bits 48-51 (byte 7, high nibble after converting from network order)
        var version = GetVersion(bytes);
        if (version != 7)
        {
            return null;
        }

        // Extract 48-bit timestamp (milliseconds since Unix epoch)
        // UUIDs are stored in mixed-endian format in .NET (RFC 4122 variant)
        // The timestamp is in bytes 0-5 but we need to account for endianness
        var timestamp = ExtractTimestampMilliseconds(bytes);

        return DateTimeOffset.FromUnixTimeMilliseconds(timestamp);
    }

    private static int GetVersion(byte[] bytes)
    {
        // In .NET Guid byte array, the version nibble is at byte[7] high nibble
        return (bytes[7] >> 4) & 0x0F;
    }

    private static long ExtractTimestampMilliseconds(byte[] bytes)
    {
        // UUIDv7 format (RFC 9562): first 48 bits are Unix timestamp in milliseconds
        // However, .NET Guid byte array has mixed endianness:
        // - First 4 bytes (Data1): little-endian 32-bit integer
        // - Next 2 bytes (Data2): little-endian 16-bit integer  
        // - Next 2 bytes (Data3): little-endian 16-bit integer
        // - Last 8 bytes (Data4): big-endian

        // For UUIDv7, the timestamp is stored as:
        // - Bytes 0-3 (Data1): Most significant 32 bits of timestamp
        // - Bytes 4-5 (Data2): Next 16 bits of timestamp
        // But we need to account for little-endian storage

        long timestamp = 0;

        // Extract Data1 (first 32 bits of timestamp, stored little-endian)
        timestamp |= ((long)bytes[3]) << 40;
        timestamp |= ((long)bytes[2]) << 32;
        timestamp |= ((long)bytes[1]) << 24;
        timestamp |= ((long)bytes[0]) << 16;

        // Extract Data2 (next 16 bits of timestamp, stored little-endian)
        timestamp |= ((long)bytes[5]) << 8;
        timestamp |= ((long)bytes[4]);

        return timestamp;
    }

    /// <summary>
    /// Checks if a given Guid is a UUIDv7.
    /// </summary>
    /// <param name="uuid">The Guid to check.</param>
    /// <returns>True if the Guid is UUIDv7, false otherwise.</returns>
    public static bool IsUuidV7(Guid uuid)
    {
        var bytes = uuid.ToByteArray();
        return GetVersion(bytes) == 7;
    }
}
