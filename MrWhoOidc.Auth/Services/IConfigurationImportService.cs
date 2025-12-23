namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for importing OIDC configuration from portable JSON format.
/// </summary>
public interface IConfigurationImportService
{
    /// <summary>
    /// Validates and previews an import without applying changes.
    /// </summary>
    /// <param name="manifest">The export manifest to preview.</param>
    /// <param name="options">Import options for context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Preview information including validation errors and conflicts.</returns>
    Task<Seeding.ImportPreview> PreviewImportAsync(
        Seeding.ExportManifest manifest,
        Seeding.ImportOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a tenant configuration, creating or updating entities as specified.
    /// </summary>
    /// <param name="manifest">The export manifest to import.</param>
    /// <param name="options">Import options including conflict resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the import operation.</returns>
    Task<Seeding.ImportResult> ImportTenantAsync(
        Seeding.ExportManifest manifest,
        Seeding.ImportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a realm configuration into an existing tenant.
    /// </summary>
    /// <param name="manifest">The export manifest containing the realm.</param>
    /// <param name="options">Import options including target tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the import operation.</returns>
    Task<Seeding.ImportResult> ImportRealmAsync(
        Seeding.ExportManifest manifest,
        Seeding.ImportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a client configuration into an existing realm.
    /// </summary>
    /// <param name="manifest">The export manifest containing the client.</param>
    /// <param name="options">Import options including target realm.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the import operation.</returns>
    Task<Seeding.ImportResult> ImportClientAsync(
        Seeding.ExportManifest manifest,
        Seeding.ImportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports an identity provider configuration into an existing tenant.
    /// </summary>
    /// <param name="manifest">The export manifest containing the provider.</param>
    /// <param name="options">Import options including target tenant.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the import operation.</returns>
    Task<Seeding.ImportResult> ImportIdentityProviderAsync(
        Seeding.ExportManifest manifest,
        Seeding.ImportOptions options,
        CancellationToken cancellationToken = default);
}
