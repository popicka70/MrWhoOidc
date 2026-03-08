using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MrWhoOidc.Auth.Persistence;
using MrWhoOidc.Auth.Crypto;
using MrWhoOidc.Auth.Protocols;
using System.Security.Cryptography;
using System.Text.Json;

namespace MrWhoOidc.Auth.Services;

/// <summary>
/// Service for orchestrating the OBO (On-Behalf-Of) setup flow.
/// Handles creation of UI client, API client, OBO policy configuration, and user assignments.
/// </summary>
public interface IOboSetupOrchestrator
{
    /// <summary>
    /// Provisions a complete OBO application setup from scratch.
    /// Creates UI client, API client(s), OBO policy, and user assignments transactionally.
    /// </summary>
    Task<OboProvisioningResult> ProvisionOboSetupAsync(OboSetupRequest request, CancellationToken ct = default);

    /// <summary>
    /// Configures OBO on existing clients (UI client calls existing API client).
    /// Skips UI/API client creation and focuses on OBO policy configuration and user assignment.
    /// </summary>
    Task<OboProvisioningResult> ConfigureExistingClientsForOboAsync(OboExistingClientRequest request, CancellationToken ct = default);

    /// <summary>
    /// Lists available clients in a tenant that could be used as UI clients (for existing-client mode).
    /// </summary>
    Task<List<ClientViewModelForSelection>> ListAvailableUiClientsAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Lists available clients in a tenant that could be used as API clients (for existing-client mode).
    /// </summary>
    Task<List<ClientViewModelForSelection>> ListAvailableApiClientsAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>
/// Request model for OBO setup provisioning.
/// </summary>
public class OboSetupRequest
{
    /// <summary>
    /// Tenant ID context.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Realm ID where clients will be created.
    /// </summary>
    public Guid RealmId { get; set; }

    /// <summary>
    /// Display name for the solution (e.g., "My App").
    /// </summary>
    public string SolutionName { get; set; } = string.Empty;

    /// <summary>
    /// UI client name.
    /// </summary>
    public string UiClientName { get; set; } = string.Empty;

    /// <summary>
    /// UI client ID (must be unique within realm).
    /// </summary>
    public string UiClientId { get; set; } = string.Empty;

    /// <summary>
    /// Redirect URIs for the UI client (login flow).
    /// </summary>
    public List<string> UiRedirectUris { get; set; } = new();

    /// <summary>
    /// Post-logout redirect URIs for the UI client.
    /// </summary>
    public List<string> UiPostLogoutRedirectUris { get; set; } = new();

    /// <summary>
    /// Whether the UI client is public (PKCE) or confidential (secret).
    /// </summary>
    public bool UiClientIsPublic { get; set; }

    /// <summary>
    /// Whether PKCE is required for the UI client.
    /// </summary>
    public bool UiClientRequirePkce { get; set; } = true;

    /// <summary>
    /// Whether consent is required for the UI client.
    /// </summary>
    public bool UiClientRequireConsent { get; set; } = false;

    /// <summary>
    /// API client name (downstream resource).
    /// </summary>
    public string ApiClientName { get; set; } = string.Empty;

    /// <summary>
    /// API client ID (e.g., "api-a", "https://api.example.com").
    /// </summary>
    public string ApiClientId { get; set; } = string.Empty;

    /// <summary>
    /// Target audience for the API (used in token exchange).
    /// </summary>
    public string ApiAudience { get; set; } = string.Empty;

    /// <summary>
    /// Scopes to be delegated via OBO (e.g., ["read", "write"]).
    /// </summary>
    public List<string> ApiDelegatedScopes { get; set; } = new();

    /// <summary>
    /// Maximum delegation depth (default 1 for single-hop only).
    /// </summary>
    public int OboMaxDelegationDepth { get; set; } = 1;

    /// <summary>
    /// Maximum lifetime for exchanged tokens in minutes (default 15).
    /// </summary>
    public int OboMaxLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// DPoP bridging mode: "Deny", "RequireSameJkt", "AllowSameJktOnly".
    /// </summary>
    public string OboDpopMode { get; set; } = "Deny";

    /// <summary>
    /// User IDs to assign to the UI client.
    /// </summary>
    public List<Guid> UserIdsToAssign { get; set; } = new();

    /// <summary>
    /// Whether to enable auto-assignment for future users.
    /// </summary>
    public bool EnableAutoAssignNewUsers { get; set; }

    /// <summary>
    /// Username of the admin performing the provisioning (for audit).
    /// </summary>
    public string ProvisionedBy { get; set; } = string.Empty;
}

/// <summary>
/// Request model for configuring existing clients for OBO.
/// </summary>
public class OboExistingClientRequest
{
    /// <summary>
    /// Tenant ID context.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Realm ID where clients are located.
    /// </summary>
    public Guid RealmId { get; set; }

    /// <summary>
    /// Display name for the setup (e.g., "My App").
    /// </summary>
    public string SolutionName { get; set; } = string.Empty;

    /// <summary>
    /// Existing UI client ID (internal database ID).
    /// </summary>
    public Guid UiClientId { get; set; }

    /// <summary>
    /// Existing API client ID (internal database ID).
    /// </summary>
    public Guid ApiClientId { get; set; }

    /// <summary>
    /// Target audience for the API (used in token exchange).
    /// </summary>
    public string ApiAudience { get; set; } = string.Empty;

    /// <summary>
    /// Scopes to be delegated via OBO (e.g., ["read", "write"]).
    /// </summary>
    public List<string> ApiDelegatedScopes { get; set; } = new();

    /// <summary>
    /// Maximum delegation depth (default 1 for single-hop only).
    /// </summary>
    public int OboMaxDelegationDepth { get; set; } = 1;

    /// <summary>
    /// Maximum lifetime for exchanged tokens in minutes (default 15).
    /// </summary>
    public int OboMaxLifetimeMinutes { get; set; } = 15;

    /// <summary>
    /// DPoP bridging mode: "Deny", "RequireSameJkt", "AllowSameJktOnly".
    /// </summary>
    public string OboDpopMode { get; set; } = "Deny";

    /// <summary>
    /// User IDs to assign to the UI client.
    /// </summary>
    public List<Guid> UserIdsToAssign { get; set; } = new();

    /// <summary>
    /// Whether to enable auto-assignment for future users.
    /// </summary>
    public bool EnableAutoAssignNewUsers { get; set; }

    /// <summary>
    /// Username of the admin performing the configuration (for audit).
    /// </summary>
    public string ProvisionedBy { get; set; } = string.Empty;
}

/// <summary>
/// View model for client selection in the UI (lightweight).
/// </summary>
public class ClientViewModelForSelection
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientName { get; set; } = string.Empty;
    public string? RealmName { get; set; }
}

/// <summary>
/// Result of OBO setup provisioning.
/// </summary>
public class OboProvisioningResult
{
    /// <summary>
    /// Whether the provisioning succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if provisioning failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Created UI client ID (internal database ID).
    /// </summary>
    public Guid UiClientRecordId { get; set; }

    /// <summary>
    /// Created UI client public ID.
    /// </summary>
    public string UiClientId { get; set; } = string.Empty;

    /// <summary>
    /// Created API client ID (internal database ID).
    /// </summary>
    public Guid ApiClientRecordId { get; set; }

    /// <summary>
    /// Created API client public ID.
    /// </summary>
    public string ApiClientId { get; set; } = string.Empty;

    /// <summary>
    /// Generated initial client secret (displayed once to admin).
    /// Only populated if UI client is confidential.
    /// </summary>
    public string? GeneratedUiClientSecret { get; set; }

    /// <summary>
    /// Number of users assigned to the UI client.
    /// </summary>
    public int UsersAssignedCount { get; set; }

    /// <summary>
    /// Timestamp of provisioning.
    /// </summary>
    public DateTime ProvisionedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class OboSetupOrchestrator(
    AuthDbContext db,
    IPasswordHasher hasher,
    ILogger<OboSetupOrchestrator> logger) : IOboSetupOrchestrator
{
    public async Task<OboProvisioningResult> ProvisionOboSetupAsync(OboSetupRequest request, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("🚀 Starting OBO setup provisioning for solution {SolutionName}", request.SolutionName);

            // Validate input
            if (string.IsNullOrWhiteSpace(request.UiClientId) || string.IsNullOrWhiteSpace(request.ApiClientId))
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = "UI Client ID and API Client ID are required."
                };
            }

            // Check for duplicate client IDs
            var existingUiClient = await db.Clients
                .FirstOrDefaultAsync(c => c.ClientId == request.UiClientId && c.TenantId == request.TenantId, ct);
            if (existingUiClient != null)
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = $"UI Client ID '{request.UiClientId}' already exists in this tenant."
                };
            }

            var existingApiClient = await db.Clients
                .FirstOrDefaultAsync(c => c.ClientId == request.ApiClientId && c.TenantId == request.TenantId, ct);
            if (existingApiClient != null)
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = $"API Client ID '{request.ApiClientId}' already exists in this tenant."
                };
            }

            // Use execution strategy for retrying transactions
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await db.Database.BeginTransactionAsync(ct);

                try
                {
                // 1. Create UI Client
                var uiClient = new Client
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,
                    RealmId = request.RealmId,
                    ClientId = request.UiClientId,
                    ClientName = request.UiClientName,
                    RequireConsent = request.UiClientRequireConsent,
                    RequirePkce = request.UiClientRequirePkce,
                    AllowLocalLogin = true,
                    AllowExternalIdp = true,
                    SubjectType = OidcConstants.SubjectTypes.Public
                };

                // Set redirect URIs
                uiClient.AllowedLoginRedirectUrisJson = JsonSerializer.Serialize(request.UiRedirectUris);
                uiClient.AllowedLogoutRedirectUrisJson = JsonSerializer.Serialize(request.UiPostLogoutRedirectUris);

                db.Clients.Add(uiClient);
                await db.SaveChangesAsync(ct);

                logger.LogInformation("✅ Created UI client {UiClientId} (record ID: {RecordId})", request.UiClientId, uiClient.Id);

                // 2. Create API Client with OBO enabled
                var apiClient = new Client
                {
                    Id = Guid.NewGuid(),
                    TenantId = request.TenantId,
                    RealmId = request.RealmId,
                    ClientId = request.ApiClientId,
                    ClientName = request.ApiClientName,
                    RequireConsent = false,
                    RequirePkce = false,
                    AllowLocalLogin = false,
                    AllowExternalIdp = false,
                    SubjectType = OidcConstants.SubjectTypes.Public
                };

                // Configure OBO policy
                apiClient.OboEnabled = true;
                apiClient.OboAllowedCallersJson = JsonSerializer.Serialize(new[] { request.UiClientId });
                apiClient.OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { uiClient.ClientId });
                apiClient.OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { request.ApiAudience });
                apiClient.OboAllowedScopesJson = JsonSerializer.Serialize(request.ApiDelegatedScopes);
                apiClient.OboMaxDelegationDepth = request.OboMaxDelegationDepth;
                apiClient.OboMaxLifetimeMinutes = request.OboMaxLifetimeMinutes;

                if (Enum.TryParse<OboDpopMode>(request.OboDpopMode, out var dpopMode))
                {
                    apiClient.OboDpopMode = dpopMode;
                }
                else
                {
                    apiClient.OboDpopMode = OboDpopMode.Deny;
                }

                db.Clients.Add(apiClient);
                await db.SaveChangesAsync(ct);

                logger.LogInformation("✅ Created API client {ApiClientId} (record ID: {RecordId}) with OBO enabled", request.ApiClientId, apiClient.Id);

                // 3. Generate initial secret for UI client if confidential
                string? generatedSecret = null;
                if (!request.UiClientIsPublic)
                {
                    generatedSecret = GenerateClientSecret();
                    var secretHash = hasher.Hash(generatedSecret);

                    var clientSecret = new ClientSecret
                    {
                        Id = Guid.NewGuid(),
                        ClientId = uiClient.Id,
                        SecretHash = secretHash,
                        CreatedAtUtc = DateTime.UtcNow,
                        Description = "Initial secret generated by OBO setup wizard",
                        ActivatedAtUtc = DateTime.UtcNow,
                        ExpiresAtUtc = DateTime.UtcNow.AddDays(90) // 90-day default expiry
                    };

                    db.ClientSecrets.Add(clientSecret);
                    await db.SaveChangesAsync(ct);

                    logger.LogInformation("✅ Created initial client secret for UI client {UiClientId}", request.UiClientId);
                }

                // 4. Assign users to UI client
                int usersAssigned = 0;
                if (request.UserIdsToAssign.Any())
                {
                    var assignments = request.UserIdsToAssign
                        .Select(userId => new UserClientAssignment
                        {
                            UserId = userId,
                            ClientId = uiClient.Id,
                            RealmId = request.RealmId,
                            IsActive = true
                        })
                        .ToList();

                    db.UserClientAssignments.AddRange(assignments);
                    await db.SaveChangesAsync(ct);

                    usersAssigned = assignments.Count;
                    logger.LogInformation("✅ Assigned {UserCount} users to UI client {UiClientId}", usersAssigned, request.UiClientId);
                }

                // 5. Enable auto-assignment if requested
                if (request.EnableAutoAssignNewUsers)
                {
                    uiClient.AutoApprovalMode = AutoApprovalMode.All;
                    uiClient.AutoAssignNewUsersToClient = true;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("✅ Enabled auto-assignment for new users to UI client {UiClientId}", request.UiClientId);
                }

                    await transaction.CommitAsync(ct);

                    logger.LogInformation("🎉 OBO setup provisioning completed successfully for {SolutionName}", request.SolutionName);

                    return new OboProvisioningResult
                    {
                        Success = true,
                        UiClientRecordId = uiClient.Id,
                        UiClientId = uiClient.ClientId,
                        ApiClientRecordId = apiClient.Id,
                        ApiClientId = apiClient.ClientId,
                        GeneratedUiClientSecret = generatedSecret,
                        UsersAssignedCount = usersAssigned,
                        ProvisionedAtUtc = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(ct);
                    logger.LogError(ex, "❌ OBO setup provisioning failed for {SolutionName}", request.SolutionName);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Unexpected error during OBO setup provisioning");
            return new OboProvisioningResult
            {
                Success = false,
                ErrorMessage = $"Provisioning failed: {ex.Message}"
            };
        }
    }

    private string GenerateClientSecret()
    {
        // Generate a secure random client secret (43 characters, URL-safe base64)
        var randomBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(randomBytes);
        }

        return Convert.ToBase64String(randomBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public async Task<OboProvisioningResult> ConfigureExistingClientsForOboAsync(OboExistingClientRequest request, CancellationToken ct = default)
    {
        try
        {
            logger.LogInformation("🔧 Starting OBO configuration for existing clients: UI={UiClientId}, API={ApiClientId}", request.UiClientId, request.ApiClientId);

            // Load both clients
            var uiClient = await db.Clients.FirstOrDefaultAsync(c => c.Id == request.UiClientId && c.TenantId == request.TenantId, ct);
            if (uiClient == null)
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = "UI client not found or does not belong to this tenant."
                };
            }

            var apiClient = await db.Clients.FirstOrDefaultAsync(c => c.Id == request.ApiClientId && c.TenantId == request.TenantId, ct);
            if (apiClient == null)
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = "API client not found or does not belong to this tenant."
                };
            }

            // Prevent circular configuration (same client cannot be both UI and API for this setup)
            if (uiClient.Id == apiClient.Id)
            {
                return new OboProvisioningResult
                {
                    Success = false,
                    ErrorMessage = "UI client and API client must be different."
                };
            }

            // Use execution strategy for retrying transactions
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                using var transaction = await db.Database.BeginTransactionAsync(ct);

                try
                {
                // 1. Update API client with OBO policy
                apiClient.OboEnabled = true;
                apiClient.OboAllowedCallersJson = JsonSerializer.Serialize(new[] { uiClient.ClientId });
                apiClient.OboAllowedSourceAudiencesJson = JsonSerializer.Serialize(new[] { uiClient.ClientId });
                apiClient.OboAllowedTargetAudiencesJson = JsonSerializer.Serialize(new[] { request.ApiAudience });
                apiClient.OboAllowedScopesJson = JsonSerializer.Serialize(request.ApiDelegatedScopes);
                apiClient.OboMaxDelegationDepth = request.OboMaxDelegationDepth;
                apiClient.OboMaxLifetimeMinutes = request.OboMaxLifetimeMinutes;

                if (Enum.TryParse<OboDpopMode>(request.OboDpopMode, out var dpopMode))
                {
                    apiClient.OboDpopMode = dpopMode;
                }
                else
                {
                    apiClient.OboDpopMode = OboDpopMode.Deny;
                }

                await db.SaveChangesAsync(ct);
                logger.LogInformation("✅ Configured OBO policy on API client {ApiClientId}", apiClient.ClientId);

                // 2. Assign users to UI client (de-duplicate with existing)
                int usersAssigned = 0;
                if (request.UserIdsToAssign.Any())
                {
                    var existingAssignments = await db.UserClientAssignments
                        .Where(a => a.ClientId == uiClient.Id && a.RealmId == request.RealmId)
                        .Select(a => a.UserId)
                        .ToListAsync(ct);

                    var newAssignments = request.UserIdsToAssign
                        .Except(existingAssignments)
                        .Select(userId => new UserClientAssignment
                        {
                            UserId = userId,
                            ClientId = uiClient.Id,
                            RealmId = request.RealmId,
                            IsActive = true
                        })
                        .ToList();

                    if (newAssignments.Any())
                    {
                        db.UserClientAssignments.AddRange(newAssignments);
                        await db.SaveChangesAsync(ct);
                        usersAssigned = newAssignments.Count;
                        logger.LogInformation("✅ Assigned {UserCount} new users to UI client {UiClientId}", usersAssigned, uiClient.ClientId);
                    }
                }

                // 3. Enable auto-assignment if requested
                if (request.EnableAutoAssignNewUsers)
                {
                    uiClient.AutoApprovalMode = AutoApprovalMode.All;
                    uiClient.AutoAssignNewUsersToClient = true;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("✅ Enabled auto-assignment for new users to UI client {UiClientId}", uiClient.ClientId);
                }

                    await transaction.CommitAsync(ct);

                    logger.LogInformation("🎉 OBO configuration completed successfully for {SolutionName}", request.SolutionName);

                    return new OboProvisioningResult
                    {
                        Success = true,
                        UiClientRecordId = uiClient.Id,
                        UiClientId = uiClient.ClientId,
                        ApiClientRecordId = apiClient.Id,
                        ApiClientId = apiClient.ClientId,
                        UsersAssignedCount = usersAssigned,
                        ProvisionedAtUtc = DateTime.UtcNow
                    };
                }
                catch (Exception ex)
                {
                    await transaction.RollbackAsync(ct);
                    logger.LogError(ex, "❌ OBO configuration failed for {SolutionName}", request.SolutionName);
                    throw;
                }
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "❌ Unexpected error during OBO configuration");
            return new OboProvisioningResult
            {
                Success = false,
                ErrorMessage = $"Configuration failed: {ex.Message}"
            };
        }
    }

    public async Task<List<ClientViewModelForSelection>> ListAvailableUiClientsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await db.Clients
            .Where(c => c.TenantId == tenantId)
            .Join(db.Realms,
                client => client.RealmId,
                realm => realm.Id,
                (client, realm) => new ClientViewModelForSelection
                {
                    Id = client.Id,
                    ClientId = client.ClientId,
                    ClientName = client.ClientName ?? client.ClientId,
                    RealmName = realm.Name
                })
            .OrderBy(c => c.ClientName)
            .ToListAsync(ct);
    }

    public async Task<List<ClientViewModelForSelection>> ListAvailableApiClientsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await db.Clients
            .Where(c => c.TenantId == tenantId)
            .Join(db.Realms,
                client => client.RealmId,
                realm => realm.Id,
                (client, realm) => new ClientViewModelForSelection
                {
                    Id = client.Id,
                    ClientId = client.ClientId,
                    ClientName = client.ClientName ?? client.ClientId,
                    RealmName = realm.Name
                })
            .OrderBy(c => c.ClientName)
            .ToListAsync(ct);
    }
}
