using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Platform-managed initial access tokens for RFC 7591 dynamic client registration.
/// Only the hash is stored.
/// </summary>
public sealed class PlatformInitialAccessToken
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// SHA-256 hash (Base64) of the plaintext token.
    /// </summary>
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RevokedAt { get; set; }

    [MaxLength(256)]
    public string? CreatedBy { get; set; }

    [MaxLength(256)]
    public string? RevokedBy { get; set; }

    [MaxLength(200)]
    public string? Description { get; set; }
}
