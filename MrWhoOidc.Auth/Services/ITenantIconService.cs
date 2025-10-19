namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for managing tenant icon uploads and retrieval.
/// </summary>
public interface ITenantIconService
{
    /// <summary>
    /// Uploads and stores a new tenant icon, replacing any existing one.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="fileName">Original filename</param>
    /// <param name="contentType">MIME type (image/png, image/jpeg, etc.)</param>
    /// <param name="fileData">The image data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The ID of the created TenantIcon</returns>
    /// <exception cref="InvalidOperationException">When file validation fails</exception>
    Task<Guid> UploadIconAsync(Guid tenantId, string fileName, string contentType, byte[] fileData, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a tenant icon by ID.
    /// </summary>
    /// <param name="iconId">The icon ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The icon data, or null if not found</returns>
    Task<TenantIconData?> GetIconAsync(Guid iconId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a tenant's current icon by tenant ID.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The icon data, or null if no icon is set</returns>
    Task<TenantIconData?> GetTenantIconAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a tenant's icon.
    /// </summary>
    /// <param name="tenantId">The tenant ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if an icon was deleted, false if no icon was set</returns>
    Task<bool> DeleteTenantIconAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents tenant icon data for retrieval operations.
/// </summary>
public class TenantIconData
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public byte[] FileData { get; set; } = Array.Empty<byte>();
    public long FileSize { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}