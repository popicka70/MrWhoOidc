using System.ComponentModel.DataAnnotations;

namespace MrWhoOidc.Auth.Persistence;

/// <summary>
/// Represents an uploaded icon/logo image for a tenant.
/// Stores the actual image data in the database for complete control and isolation.
/// </summary>
public class TenantIcon
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The tenant this icon belongs to
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Navigation property to the tenant
    /// </summary>
    public Tenant Tenant { get; set; } = null!;

    /// <summary>
    /// Original filename of the uploaded image
    /// </summary>
    [MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME content type (e.g., image/png, image/jpeg, image/svg+xml)
    /// </summary>
    [MaxLength(100)]
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// The actual image data as bytes
    /// </summary>
    public byte[] FileData { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long FileSize { get; set; }

    /// <summary>
    /// When the icon was uploaded
    /// </summary>
    public DateTimeOffset UploadedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Width of the image in pixels (optional, for display optimization)
    /// </summary>
    public int? Width { get; set; }

    /// <summary>
    /// Height of the image in pixels (optional, for display optimization)
    /// </summary>
    public int? Height { get; set; }
}
