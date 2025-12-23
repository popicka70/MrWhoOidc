namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for exporting OIDC configuration to portable JSON format.
/// </summary>
public interface IConfigurationExportService
{
    /// <summary>
    /// Exports a tenant's complete configuration including realms, clients, and identity providers.
    /// </summary>
    /// <param name="tenantId">The tenant ID to export.</param>
    /// <param name="options">Export options (mode, metadata, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export manifest containing the tenant configuration.</returns>
    Task<Seeding.ExportManifest> ExportTenantAsync(
        Guid tenantId,
        Seeding.ExportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a realm's configuration including its clients.
    /// </summary>
    /// <param name="realmId">The realm ID to export.</param>
    /// <param name="options">Export options (mode, metadata, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export manifest containing the realm configuration.</returns>
    Task<Seeding.ExportManifest> ExportRealmAsync(
        Guid realmId,
        Seeding.ExportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a single client's complete configuration.
    /// </summary>
    /// <param name="clientId">The client database ID to export.</param>
    /// <param name="options">Export options (mode, metadata, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export manifest containing the client configuration.</returns>
    Task<Seeding.ExportManifest> ExportClientAsync(
        Guid clientId,
        Seeding.ExportOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports an identity provider's configuration including claim mappings.
    /// </summary>
    /// <param name="providerId">The identity provider ID to export.</param>
    /// <param name="options">Export options (mode, metadata, etc.).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The export manifest containing the provider configuration.</returns>
    Task<Seeding.ExportManifest> ExportIdentityProviderAsync(
        Guid providerId,
        Seeding.ExportOptions options,
        CancellationToken cancellationToken = default);
}
