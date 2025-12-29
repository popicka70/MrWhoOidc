namespace MrWhoOidc.Auth.Options;

/// <summary>
/// Configuration options for file uploads.
/// </summary>
public class UploadOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Uploads";
    
    /// <summary>
    /// Base path for file uploads. Defaults to "./data/uploads" relative to content root.
    /// Can be set to an absolute path like "/app/data/uploads" for Docker.
    /// </summary>
    public string BasePath { get; set; } = "./data/uploads";
    
    /// <summary>
    /// URL path prefix for serving uploaded files. Defaults to "/uploads".
    /// </summary>
    public string UrlPathPrefix { get; set; } = "/uploads";
    
    /// <summary>
    /// Maximum file size in bytes. Defaults to 512 KB.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 512 * 1024;
    
    /// <summary>
    /// Allowed file extensions for uploads.
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [".png", ".jpg", ".jpeg", ".svg", ".webp"];
}
