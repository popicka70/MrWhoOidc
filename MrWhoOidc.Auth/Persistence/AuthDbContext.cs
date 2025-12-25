using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MrWhoOidc.Auth.Licensing.Entities;
using MrWhoOidc.Auth.Persistence.Configurations;
using Microsoft.Extensions.Logging;

namespace MrWhoOidc.Auth.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    private ILogger<AuthDbContext>? _logger;

    // Multi-tenancy
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantIcon> TenantIcons => Set<TenantIcon>();

    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<UserTenantMembership> UserTenantMemberships => Set<UserTenantMembership>();
    public DbSet<User> Users => Set<User>();
    public DbSet<WebAuthnCredential> WebAuthnCredentials => Set<WebAuthnCredential>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<ClientSecret> ClientSecrets => Set<ClientSecret>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<AuthorizationCode> AuthorizationCodes => Set<AuthorizationCode>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<Token> Tokens => Set<Token>();
    public DbSet<RevocationAudit> RevocationAudits => Set<RevocationAudit>();
    public DbSet<PushedAuthorizationRequest> PushedAuthorizationRequests => Set<PushedAuthorizationRequest>();
    public DbSet<Realm> Realms => Set<Realm>();
    // New: roles/scopes and assignments
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Scope> Scopes => Set<Scope>();
    public DbSet<ClientScope> ClientScopes => Set<ClientScope>();
    public DbSet<UserAlternativeEmail> UserAlternativeEmails => Set<UserAlternativeEmail>();
    public DbSet<EmailConfirmation> EmailConfirmations => Set<EmailConfirmation>();
    public DbSet<UserClientAssignment> UserClientAssignments => Set<UserClientAssignment>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    // New: split role assignments
    public DbSet<UserRealmRoleAssignment> UserRealmRoleAssignments => Set<UserRealmRoleAssignment>();
    public DbSet<UserClientRoleAssignment> UserClientRoleAssignments => Set<UserClientRoleAssignment>();
    // New: registrations
    public DbSet<Registration> Registrations => Set<Registration>();
    // New: IdP chaining
    public DbSet<IdentityProvider> IdentityProviders => Set<IdentityProvider>();
    public DbSet<ClientIdentityProvider> ClientIdentityProviders => Set<ClientIdentityProvider>();
    public DbSet<IdentityProviderClaimMapping> IdentityProviderClaimMappings => Set<IdentityProviderClaimMapping>();
    public DbSet<IdentityProviderKey> IdentityProviderKeys => Set<IdentityProviderKey>();
    // New: Client JWKS history (for admin diagnostics)
    public DbSet<ClientJwksHistory> ClientJwksHistories => Set<ClientJwksHistory>();
    // New: External identities (issuer+sub linkage)
    public DbSet<ExternalIdentity> ExternalIdentities => Set<ExternalIdentity>();
    // New: Back-channel logout outbox
    public DbSet<BackchannelLogoutNotification> BackchannelLogoutNotifications => Set<BackchannelLogoutNotification>();
    // New: Opaque logout redirect references (post_logout_redirect_uri indirection)
    public DbSet<LogoutRedirectReference> LogoutRedirectReferences => Set<LogoutRedirectReference>();
    // New: QR code login sessions
    public DbSet<QrLoginSession> QrLoginSessions => Set<QrLoginSession>();
    // New: Impersonation audit logs
    public DbSet<ImpersonationAuditLog> ImpersonationAuditLogs => Set<ImpersonationAuditLog>();
    // New: Password reset tokens (global, tied to UserAccount)
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    // Licensing
    public DbSet<License> Licenses => Set<License>();
    public DbSet<LicenseHistoryEntry> LicenseHistory => Set<LicenseHistoryEntry>();
    public DbSet<FeatureUsageMetric> FeatureUsageMetrics => Set<FeatureUsageMetric>();
    public DbSet<LicenseLimit> LicenseLimits => Set<LicenseLimit>();
    // New: Configuration export/import audit logs
    public DbSet<MrWhoOidc.Auth.Seeding.ConfigurationAuditLog> ConfigurationAuditLogs => Set<MrWhoOidc.Auth.Seeding.ConfigurationAuditLog>();

    // IDataProtectionKeyContext requirement
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureUserPrimaryKeysAvailableAsync(CancellationToken.None).GetAwaiter().GetResult();
        NormalizeEmailFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureUserPrimaryKeysAvailableAsync(cancellationToken).ConfigureAwait(false);
        NormalizeEmailFields();
        return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        await EnsureUserPrimaryKeysAvailableAsync(cancellationToken).ConfigureAwait(false);
        NormalizeEmailFields();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureUserPrimaryKeysAvailableAsync(CancellationToken cancellationToken)
    {
        var pendingUsers = ChangeTracker.Entries<User>()
            .Where(e => e.State == EntityState.Added)
            .ToList();

        if (pendingUsers.Count == 0)
        {
            return;
        }

        var desiredIds = pendingUsers
            .Select(e => e.Entity.Id == Guid.Empty ? GuidHelper.NewId() : e.Entity.Id)
            .ToList();

        for (var i = 0; i < pendingUsers.Count; i++)
        {
            if (pendingUsers[i].Entity.Id == Guid.Empty)
            {
                pendingUsers[i].Entity.Id = desiredIds[i];
            }
        }

        var existingIds = await Users.AsNoTracking()
            .Where(u => desiredIds.Contains(u.Id))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existingIds.Count == 0)
        {
            return;
        }

        foreach (var entry in pendingUsers)
        {
            if (!existingIds.Contains(entry.Entity.Id))
            {
                continue;
            }

            Guid newId;
            do
            {
                newId = GuidHelper.NewId();
            }
            while (desiredIds.Contains(newId));

            while (await Users.AsNoTracking().AnyAsync(u => u.Id == newId, cancellationToken).ConfigureAwait(false))
            {
                newId = GuidHelper.NewId();
            }

            Logger?.LogWarning("Detected duplicate User.Id {UserId} while saving; reassigned to {NewUserId}", entry.Entity.Id, newId);

            entry.Entity.Id = newId;
            desiredIds.Add(newId);
        }
    }

    private ILogger<AuthDbContext>? Logger => _logger ??= this.GetService<ILogger<AuthDbContext>>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Multi-tenancy: Tenant
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Slug).IsRequired().HasMaxLength(100);
            b.Property(x => x.Name).IsRequired().HasMaxLength(200);
            b.Property(x => x.Description).HasMaxLength(500);
            b.Property(x => x.IssuerUri).IsRequired().HasMaxLength(500);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.LogoUrl).HasMaxLength(200);
            b.Property(x => x.PrimaryColor).HasMaxLength(50);
            b.Property(x => x.AccentColor).HasMaxLength(50);
            b.Property(x => x.SettingsJson).HasMaxLength(4000);
            b.Property(x => x.AdminEmail).HasMaxLength(256);
            b.Property(x => x.BillingPlan).HasMaxLength(100);
            b.Property(x => x.MetadataJson).HasMaxLength(2000);
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasIndex(x => x.Status);
            // Relationship to TenantIcon
            b.HasOne(x => x.TenantIcon)
                .WithOne(x => x.Tenant)
                .HasForeignKey<Tenant>(x => x.TenantIconId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // Multi-tenancy: TenantIcon
        modelBuilder.Entity<TenantIcon>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.FileName).IsRequired().HasMaxLength(255);
            b.Property(x => x.ContentType).IsRequired().HasMaxLength(100);
            b.Property(x => x.FileData).IsRequired();
            b.Property(x => x.FileSize).IsRequired();
            b.Property(x => x.UploadedAt).IsRequired();
            b.HasIndex(x => x.TenantId);
            // Foreign key relationship
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserAccount>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.Username).IsUnique();
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            // Unique index on NormalizedEmail (filtered to exclude nulls)
            b.HasIndex(x => x.NormalizedEmail)
                .IsUnique()
                .HasFilter("\"NormalizedEmail\" IS NOT NULL");
            b.Property(x => x.Name).HasMaxLength(200);
            b.Property(x => x.PasswordHash).IsRequired();
            b.Property(x => x.PasswordSalt).HasMaxLength(128);
            b.Property(x => x.HashAlgorithm).IsRequired().HasMaxLength(50);
            b.Property(x => x.SecurityStamp).HasMaxLength(200);
            b.Property(x => x.SettingsJson).HasMaxLength(4000);
            b.Property(x => x.TotpSecret).HasMaxLength(200);
            b.Property(x => x.LockedOutUntil);
            // New global auth fields
            b.Property(x => x.FailedLoginAttempts).HasDefaultValue(0);
            b.Property(x => x.LastFailedLoginAt);
            b.Property(x => x.PasswordUpdatedAt);
            b.HasMany(x => x.TenantMemberships)
                .WithOne(x => x.UserAccount)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTenantMembership>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.SettingsJson).HasMaxLength(2000);
            b.Property(x => x.Status)
                .HasConversion<string>()
                .HasMaxLength(50)
                .IsRequired();
            b.HasIndex(x => new { x.UserAccountId, x.TenantId }).IsUnique();
            b.HasIndex(x => x.TenantId);
            b.HasIndex(x => x.Status);
            b.HasOne(x => x.UserAccount)
                .WithMany(x => x.TenantMemberships)
                .HasForeignKey(x => x.UserAccountId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Tenant)
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.DefaultRealm)
                .WithMany()
                .HasForeignKey(x => x.DefaultRealmId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).IsRequired().HasMaxLength(200);
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).HasMaxLength(256);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.NormalizedEmail }).IsUnique();
            b.HasMany(x => x.AlternativeEmails)
                .WithOne()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // TOTP
            b.Property(x => x.TotpSecret).HasMaxLength(200);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Realm>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.AllowUnconfirmedLogin).HasDefaultValue(true).IsRequired();
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: Role per realm
        modelBuilder.Entity<Role>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(x => new { x.RealmId, x.Name }).IsUnique();
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: Scope catalog
        modelBuilder.Entity<Scope>(b =>
        {
            b.HasKey(x => x.Name);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(200);
            
            // Multi-tenancy support
            // Composite unique index for tenant-scoped scopes: (TenantId, Name)
            b.HasIndex(x => new { x.TenantId, x.Name })
                .IsUnique()
                .HasFilter("\"TenantId\" IS NOT NULL");
            
            // Unique index for global scopes: Name must be unique among global scopes
            b.HasIndex(x => x.Name)
                .IsUnique()
                .HasFilter("\"TenantId\" IS NULL AND \"IsGlobal\" = true");
            
            // FK to Tenant for tenant-scoped scopes
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false); // Nullable for global scopes
            
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Client>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.ClientId).IsUnique();
#pragma warning disable CS0618 // Type or member is obsolete - retained for backward compatibility
            b.Property(x => x.ClientSecretHash).HasMaxLength(500);
#pragma warning restore CS0618 // Type or member is obsolete
            b.Property(x => x.RealmId).IsRequired();
            b.HasIndex(x => x.RealmId);
            b.Property(x => x.IntrospectionAudiencesJson).HasMaxLength(2000);
            // New: per-client public keys (for private_key_jwt and JAR)
            b.Property(x => x.PublicJwksJson).HasMaxLength(8000);
            b.Property(x => x.PublicJwksUri).HasMaxLength(2000);
            // New: per-client PAR requirement and introspection shaping/mTLS
            b.Property(x => x.RequirePar).HasDefaultValue(false);
            b.Property(x => x.IntrospectionResponseFieldsJson).HasMaxLength(2000);
            b.Property(x => x.IntrospectionMtlsThumbprintsJson).HasMaxLength(2000);
            // New: per-client allowed redirect URIs
            b.Property(x => x.AllowedLoginRedirectUrisJson).HasMaxLength(4000);
            b.Property(x => x.AllowedLogoutRedirectUrisJson).HasMaxLength(4000);
            // New: login methods toggles
            b.Property(x => x.AllowLocalLogin).HasDefaultValue(true);
            b.Property(x => x.AllowExternalIdp).HasDefaultValue(true);
            b.Property(x => x.AllowQrLogin).HasDefaultValue(false);
            // New: UI login style scheme key
            b.Property(x => x.LoginStyleKey).HasMaxLength(50);
            // New: M2M policy knobs
            b.Property(x => x.M2MAllowedAudiencesJson).HasMaxLength(2000);
            b.Property(x => x.M2MAccessTokenLifetimeSeconds);
            b.Property(x => x.AllowClientSecretBasic).HasDefaultValue(true);
            b.Property(x => x.AllowClientSecretPost).HasDefaultValue(true);
            b.Property(x => x.AllowPrivateKeyJwt).HasDefaultValue(true);
            b.Property(x => x.M2MMtlsThumbprintsJson).HasMaxLength(2000);
            // New: external user provisioning/linking policies
            b.Property(x => x.AllowExternalAutoProvision).HasDefaultValue(true);
            b.Property(x => x.AllowExternalEmailLinking).HasDefaultValue(true);
            b.Property(x => x.RequireEmailLinkConfirmation).HasDefaultValue(true);
            // New: Front-channel logout
            b.Property(x => x.FrontChannelLogoutUri).HasMaxLength(2000);
            b.Property(x => x.FrontChannelLogoutSessionRequired).HasDefaultValue(true);

            // New: Back-channel logout
            b.Property(x => x.BackChannelLogoutUri).HasMaxLength(2000);
            b.Property(x => x.BackChannelLogoutSessionRequired).HasDefaultValue(true);

            // OBO policy columns
            b.Property(x => x.OboEnabled);
            b.Property(x => x.OboAllowedSourceAudiencesJson).HasMaxLength(2000);
            b.Property(x => x.OboAllowedTargetAudiencesJson).HasMaxLength(2000);
            b.Property(x => x.OboAllowedScopesJson).HasMaxLength(2000);
            b.Property(x => x.OboMaxDelegationDepth);
            b.Property(x => x.OboMaxLifetimeMinutes);
            b.Property(x => x.OboDpopMode);
            b.Property(x => x.OboAllowedCallersJson).HasMaxLength(2000);

            b.Property(x => x.AutoAssignNewUsersToClient).HasDefaultValue(false);

            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: Client JWKS history
        modelBuilder.Entity<ClientJwksHistory>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.JwksJson).IsRequired().HasMaxLength(8000);
            b.Property(x => x.Source).HasMaxLength(50);
            b.Property(x => x.Hash).HasMaxLength(64);
            b.Property(x => x.CreatedAt).IsRequired();
            b.HasIndex(x => new { x.ClientId, x.CreatedAt });
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: ClientScopes mapping (ClientId -> ScopeName)
        modelBuilder.Entity<ClientScope>(b =>
        {
            b.HasKey(x => new { x.ClientId, x.ScopeName });
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Scope>()
                .WithMany()
                .HasForeignKey(x => x.ScopeName)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: ClientSecret (overlapping secrets with expiry)
        modelBuilder.Entity<ClientSecret>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.SecretHash).IsRequired().HasMaxLength(500);
            b.Property(x => x.Description).HasMaxLength(100);
            b.Property(x => x.CreatedBy).HasMaxLength(200);
            b.Property(x => x.ActivatedBy).HasMaxLength(200);
            b.Property(x => x.RevokedBy).HasMaxLength(200);
            
            // Performance index for validation queries (active secrets)
            b.HasIndex(x => new { x.ClientId, x.ActivatedAtUtc, x.RevokedAtUtc, x.ExpiresAtUtc })
                .HasDatabaseName("IX_ClientSecrets_Active");
            
            // Uniqueness: Only one primary secret per client (if not revoked)
            b.HasIndex(x => new { x.ClientId, x.IsPrimary })
                .IsUnique()
                .HasFilter("\"IsPrimary\" = TRUE AND \"RevokedAtUtc\" IS NULL")
                .HasDatabaseName("IX_ClientSecrets_PrimaryPerClient");
            
            b.HasOne(x => x.Client)
                .WithMany(x => x.ClientSecrets)
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: Alternative emails
        modelBuilder.Entity<UserAlternativeEmail>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).IsRequired().HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(256);
            b.HasIndex(x => new { x.UserId, x.NormalizedEmail }).IsUnique();
            b.HasIndex(x => x.NormalizedEmail).IsUnique();
        });

        modelBuilder.Entity<EmailConfirmation>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.TokenHash).IsRequired().HasMaxLength(100);
            b.Property(x => x.Purpose).IsRequired().HasMaxLength(50);
            b.Property(x => x.Email).IsRequired().HasMaxLength(256);
            b.Property(x => x.CreatedAt).IsRequired();
            b.Property(x => x.ExpiresAt).IsRequired();
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.Purpose, x.Email })
                .HasDatabaseName("IX_EmailConfirmations_UserPurposeEmail");
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<UserAlternativeEmail>()
                .WithMany()
                .HasForeignKey(x => x.UserAlternativeEmailId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: User-client assignment (optionally realm-bound)
        modelBuilder.Entity<UserClientAssignment>(b =>
        {
            b.HasKey(x => new { x.UserId, x.ClientId, x.RealmId });
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Legacy: User-role assignment per client + realm
        modelBuilder.Entity<UserRoleAssignment>(b =>
        {
            b.HasKey(x => new { x.UserId, x.RoleId, x.ClientId, x.RealmId });
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Role>()
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: User realm-role assignment (role granted directly in a realm)
        modelBuilder.Entity<UserRealmRoleAssignment>(b =>
        {
            b.HasKey(x => new { x.UserId, x.RoleId, x.RealmId });
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Role>()
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: User client-role assignment (role granted for a specific client)
        modelBuilder.Entity<UserClientRoleAssignment>(b =>
        {
            b.HasKey(x => new { x.UserId, x.RoleId, x.ClientId });
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Role>()
                .WithMany()
                .HasForeignKey(x => x.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: Registrations
        modelBuilder.Entity<Registration>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).IsRequired().HasMaxLength(256);
            b.Property(x => x.NormalizedEmail).IsRequired().HasMaxLength(256);
            b.Property(x => x.FirstName).HasMaxLength(100);
            b.Property(x => x.LastName).HasMaxLength(100);
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.Property(x => x.State).IsRequired().HasMaxLength(20);
            b.Property(x => x.CreatedAt).IsRequired();
            b.HasIndex(x => x.NormalizedEmail);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.SetNull); // restore original behavior
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);

            // New: Tenant creation fields
            b.Property(x => x.IsTenantAdmin).HasDefaultValue(false);
            b.Property(x => x.TenantSlug).HasMaxLength(100);
            b.Property(x => x.TenantName).HasMaxLength(200);
            b.Property(x => x.TenantDescription).HasMaxLength(500);
            // Index for tenant slug uniqueness (only for tenant admin registrations)
            b.HasIndex(x => x.TenantSlug)
                .IsUnique()
                .HasFilter("\"TenantSlug\" IS NOT NULL")
                .HasDatabaseName("IX_Registrations_TenantSlug_Unique");
        });

        modelBuilder.Entity<SigningKey>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Kid).IsRequired();
            b.HasIndex(x => x.Kid).IsUnique();
            // Multi-tenancy FK (nullable for backward compat)
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<AuthorizationCode>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.Code).IsUnique();
            b.Property(x => x.ClientId).IsRequired();
            b.Property(x => x.RedirectUri).IsRequired();
            b.Property(x => x.ScopesJson).IsRequired();
            b.Property(x => x.CodeChallengeMethod).HasMaxLength(10);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Consent>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired();
            b.HasIndex(x => new { x.UserId, x.ClientId }).IsUnique();
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<Token>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Type).IsRequired().HasMaxLength(20);
            b.Property(x => x.TokenHash).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.Property(x => x.ClientId).IsRequired();
            b.Property(x => x.ScopesJson).IsRequired();
            b.Property(x => x.Audience).HasMaxLength(200);
            b.Property(x => x.Jti).HasMaxLength(64);
            b.Property(x => x.CnfJkt).HasMaxLength(200);
            b.Property(x => x.ActJson);
            b.Property(x => x.DelegationDepth).HasDefaultValue(0);
            // Session metadata (Phase 5B Feature 3)
            b.Property(x => x.IpAddress).HasMaxLength(100);
            b.Property(x => x.UserAgent).HasMaxLength(500);
            b.HasIndex(x => new { x.UserId, x.ClientId, x.Type });
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<RevocationAudit>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired();
            b.Property(x => x.TokenHash).IsRequired().HasMaxLength(200);
            b.Property(x => x.TokenType).HasMaxLength(20);
            b.Property(x => x.IpAddress).HasMaxLength(100);
            b.HasIndex(x => new { x.TokenHash, x.ClientId });
        });

        modelBuilder.Entity<PushedAuthorizationRequest>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.RequestUri).HasMaxLength(512);
            b.Property(x => x.ClientId).IsRequired();
            b.Property(x => x.RequestJson).IsRequired();
            b.Property(x => x.CreatedAt).IsRequired();
            b.Property(x => x.ExpiresAt).IsRequired();
            b.Property(x => x.Consumed).IsRequired();
            b.HasIndex(x => x.ExpiresAt);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: IdentityProvider
        modelBuilder.Entity<IdentityProvider>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(150);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.Type).IsRequired();
            b.Property(x => x.Enabled).HasDefaultValue(true);
            b.Property(x => x.IsDefault).HasDefaultValue(false);
            b.Property(x => x.LogoUrl).HasMaxLength(2000);
            b.Property(x => x.SortOrder).HasDefaultValue(0);
            b.Property(x => x.ConfigJson).HasMaxLength(8000);
            b.Property(x => x.CreatedAt).IsRequired();
            b.Property(x => x.UpdatedAt).IsRequired();
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: ClientIdentityProvider mapping
        modelBuilder.Entity<ClientIdentityProvider>(b =>
        {
            b.HasKey(x => new { x.ClientId, x.IdentityProviderId });
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<IdentityProvider>()
                .WithMany()
                .HasForeignKey(x => x.IdentityProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Enabled).HasDefaultValue(true);
            b.Property(x => x.IsDefaultForClient).HasDefaultValue(false);
            b.Property(x => x.AutoRedirectIfSingle).HasDefaultValue(false);
            b.Property(x => x.RequiredAcr).HasMaxLength(100);
            b.Property(x => x.Order).HasDefaultValue(0);
        });

        // New: IdentityProviderClaimMapping
        modelBuilder.Entity<IdentityProviderClaimMapping>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne<IdentityProvider>()
                .WithMany()
                .HasForeignKey(x => x.IdentityProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.ExternalClaim).IsRequired().HasMaxLength(200);
            b.Property(x => x.LocalClaim).IsRequired().HasMaxLength(200);
            b.Property(x => x.Transform).HasMaxLength(200);
            b.Property(x => x.Order).HasDefaultValue(0);
        });

        // New: IdentityProviderKey
        modelBuilder.Entity<IdentityProviderKey>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasOne<IdentityProvider>()
                .WithMany()
                .HasForeignKey(x => x.IdentityProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(x => x.Purpose).IsRequired();
            b.Property(x => x.Jwk).IsRequired().HasMaxLength(8000);
            b.Property(x => x.Alg).IsRequired().HasMaxLength(20);
            b.Property(x => x.Active).HasDefaultValue(true);
            b.Property(x => x.Kid).HasMaxLength(200);
            b.Property(x => x.CreatedAt).IsRequired();
            b.HasIndex(x => new { x.IdentityProviderId, x.Kid })
                .HasDatabaseName("IX_IdentityProviderKeys_Provider_Kid_CI");
        });

        // New: ExternalIdentity linkage
        modelBuilder.Entity<ExternalIdentity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Issuer).IsRequired().HasMaxLength(2000);
            b.Property(x => x.Subject).IsRequired().HasMaxLength(400);
            b.Property(x => x.ProviderName).HasMaxLength(150);
            b.Property(x => x.ClaimsJson).HasMaxLength(4000);
            b.HasIndex(x => new { x.Issuer, x.Subject }).IsUnique();
            b.HasOne<User>()
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // New: Back-channel logout outbox
        modelBuilder.Entity<BackchannelLogoutNotification>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired();
            b.Property(x => x.TargetUri).IsRequired().HasMaxLength(2000);
            b.Property(x => x.LogoutToken).IsRequired().HasMaxLength(8000);
            b.Property(x => x.Status).IsRequired().HasMaxLength(20);
            b.Property(x => x.AttemptCount).HasDefaultValue(0);
            b.Property(x => x.MaxAttempts).HasDefaultValue(5);
            b.Property(x => x.LastHttpStatus);
            b.Property(x => x.LastError).HasMaxLength(1000);
            b.Property(x => x.CreatedAt).IsRequired();
            b.HasIndex(x => new { x.Status, x.NextAttemptAt });
            b.HasIndex(x => x.ClientId);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientDbId)
                .OnDelete(DeleteBehavior.Cascade);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
        });

        // New: Logout redirect references (opaque indirection table)
        modelBuilder.Entity<LogoutRedirectReference>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasMaxLength(64);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.Property(x => x.RedirectUri).IsRequired().HasMaxLength(2000);
            b.Property(x => x.State).HasMaxLength(400);
            b.Property(x => x.CreatedAt).IsRequired();
            b.Property(x => x.ExpiresAt).IsRequired();
            b.Property(x => x.Used).HasDefaultValue(false);
            b.HasIndex(x => x.ExpiresAt);
        });

        // New: QR login sessions
        modelBuilder.Entity<QrLoginSession>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.SessionToken).IsRequired().HasMaxLength(128);
            b.Property(x => x.SessionTokenHash).HasMaxLength(64);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.Property(x => x.ReturnUrl).IsRequired().HasMaxLength(2000);
            b.Property(x => x.CodeChallenge).IsRequired().HasMaxLength(128);
            b.Property(x => x.CodeChallengeMethod).IsRequired().HasMaxLength(10);
            b.Property(x => x.State).IsRequired().HasMaxLength(1000);
            b.Property(x => x.Nonce).HasMaxLength(200);
            b.Property(x => x.Scope).IsRequired().HasMaxLength(1000);
            b.Property(x => x.Status).IsRequired();
            b.Property(x => x.UserId);
            b.Property(x => x.AuthorizationCode).HasMaxLength(200);
            b.Property(x => x.CreatedAt).IsRequired();
            b.Property(x => x.ExpiresAt).IsRequired();
            b.Property(x => x.ScannedAt);
            b.Property(x => x.AuthenticatedAt);
            b.Property(x => x.MobileUserAgent).HasMaxLength(500);
            b.Property(x => x.MobileIpAddress).HasMaxLength(100);
            b.HasIndex(x => x.SessionToken).IsUnique();
            b.HasIndex(x => x.SessionTokenHash);
            // Multi-tenancy FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.TenantId);
            b.HasIndex(x => new { x.Status, x.ExpiresAt });
        });

        // New: Configuration export/import audit logs
        modelBuilder.Entity<MrWhoOidc.Auth.Seeding.ConfigurationAuditLog>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Operation).IsRequired().HasMaxLength(20);
            b.Property(x => x.EntityType).IsRequired().HasMaxLength(50);
            b.Property(x => x.EntityIdentifier).HasMaxLength(200);
            b.Property(x => x.ExportMode).IsRequired().HasMaxLength(20);
            b.Property(x => x.Result).IsRequired().HasMaxLength(20);
            b.Property(x => x.ErrorDetails).HasMaxLength(4000);
            b.Property(x => x.ManifestChecksum).HasMaxLength(100);
            b.Property(x => x.PerformedBy).IsRequired().HasMaxLength(256);
            b.Property(x => x.IpAddress).HasMaxLength(100);
            b.Property(x => x.UserAgent).HasMaxLength(500);
            b.Property(x => x.Timestamp).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.Timestamp })
                .IsDescending(false, true)
                .HasDatabaseName("IX_ConfigurationAuditLog_Tenant_Timestamp");
            b.HasIndex(x => new { x.Operation, x.Timestamp })
                .IsDescending(false, true)
                .HasDatabaseName("IX_ConfigurationAuditLog_Operation");
            // Optional tenant FK
            b.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.SetNull);
        });

            ConfigureLicenseEntities(modelBuilder);
        }

        static void ConfigureLicenseEntities(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);
            modelBuilder.ApplyConfiguration(new LicenseConfiguration());
            modelBuilder.ApplyConfiguration(new LicenseHistoryEntryConfiguration());
            modelBuilder.ApplyConfiguration(new FeatureUsageMetricConfiguration());
            modelBuilder.ApplyConfiguration(new LicenseLimitConfiguration());
        }

    void NormalizeEmailFields()
    {
        foreach (var entry in ChangeTracker.Entries<UserAccount>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (entry.State == EntityState.Added || entry.Property(nameof(UserAccount.Email)).IsModified)
                {
                    entry.Entity.Email = EmailNormalizer.FormatForStorage(entry.Entity.Email, required: false, out var normalized);
                    entry.Entity.NormalizedEmail = normalized;
                }
            }
        }

        foreach (var entry in ChangeTracker.Entries<User>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (entry.State == EntityState.Added || entry.Property(nameof(User.Email)).IsModified)
                {
                    entry.Entity.Email = EmailNormalizer.FormatForStorage(entry.Entity.Email, required: false, out var normalized);
                    entry.Entity.NormalizedEmail = normalized;
                }
            }
        }

        foreach (var entry in ChangeTracker.Entries<UserAlternativeEmail>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (entry.State == EntityState.Added || entry.Property(nameof(UserAlternativeEmail.Email)).IsModified)
                {
                    var formatted = EmailNormalizer.FormatForStorage(entry.Entity.Email, required: true, out var normalized)
                        ?? throw new ValidationException("Alternative email normalization returned null.");
                    entry.Entity.Email = formatted;
                    entry.Entity.NormalizedEmail = normalized ?? string.Empty;
                }
            }
        }

        foreach (var entry in ChangeTracker.Entries<Registration>())
        {
            if (entry.State == EntityState.Added || entry.State == EntityState.Modified)
            {
                if (entry.State == EntityState.Added || entry.Property(nameof(Registration.Email)).IsModified)
                {
                    var formatted = EmailNormalizer.FormatForStorage(entry.Entity.Email, required: true, out var normalized)
                        ?? throw new ValidationException("Registration email normalization returned null.");
                    entry.Entity.Email = formatted;
                    entry.Entity.NormalizedEmail = normalized ?? string.Empty;
                }
            }
        }
    }
}

public class UserAccount
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? PasswordSalt { get; set; }
    [MaxLength(50)]
    public string HashAlgorithm { get; set; } = "argon2id";
    [MaxLength(256)]
    public string? Email { get; set; }
    [MaxLength(256)]
    public string? NormalizedEmail { get; set; }
    public bool EmailVerified { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    [MaxLength(200)]
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)]
    public string? SecurityStamp { get; set; }
    [MaxLength(4000)]
    public string? SettingsJson { get; set; }
    [MaxLength(200)]
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    public DateTimeOffset? LockedOutUntil { get; set; }

    /// <summary>
    /// Counter for failed login attempts (global across all tenants).
    /// </summary>
    public int FailedLoginAttempts { get; set; }

    /// <summary>
    /// Timestamp of the last failed login attempt.
    /// </summary>
    public DateTimeOffset? LastFailedLoginAt { get; set; }

    /// <summary>
    /// Timestamp when the password was last changed.
    /// </summary>
    public DateTimeOffset? PasswordUpdatedAt { get; set; }

    public ICollection<UserTenantMembership> TenantMemberships { get; set; } = new List<UserTenantMembership>();
}

/// <summary>
/// Password reset token for global UserAccount password reset.
/// Tokens are single-use and expire after a configurable period.
/// </summary>
public class PasswordResetToken
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    /// <summary>
    /// The UserAccount this reset token belongs to.
    /// </summary>
    public Guid UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;

    /// <summary>
    /// The hashed token value (SHA256 of the raw token sent to user).
    /// </summary>
    [MaxLength(128)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>
    /// When this token was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this token expires.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Whether this token has been used.
    /// </summary>
    public bool IsUsed { get; set; }

    /// <summary>
    /// When this token was used (if applicable).
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }

    /// <summary>
    /// IP address from which the reset was requested.
    /// </summary>
    [MaxLength(50)]
    public string? RequestedFromIp { get; set; }
}

public class UserTenantMembership
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    public Guid UserAccountId { get; set; }
    public UserAccount UserAccount { get; set; } = null!;

    public Guid TenantId { get; set; }
    public Tenant Tenant { get; set; } = null!;

    public Guid? DefaultRealmId { get; set; }
    public Realm? DefaultRealm { get; set; }

    [MaxLength(200)]
    public string? DisplayName { get; set; }
    public TenantMembershipStatus Status { get; set; } = TenantMembershipStatus.Active;
    public bool IsTenantAdmin { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    [MaxLength(2000)]
    public string? SettingsJson { get; set; }
}

public enum TenantMembershipStatus
{
    Active = 0,
    Suspended = 1,
    Pending = 2,
    Revoked = 3
}

public class User
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;
    
    // Password fields REMOVED - use UserAccount.PasswordHash for authentication
    // These columns will be dropped in the next migration
    
    [MaxLength(256)]
    public string? Email { get; set; }
    [MaxLength(256)]
    public string? NormalizedEmail { get; set; }
    public bool EmailVerified { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    [MaxLength(200)]
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    // New: alternative emails
    public ICollection<UserAlternativeEmail> AlternativeEmails { get; set; } = new List<UserAlternativeEmail>();
    // MFA TOTP
    [MaxLength(200)]
    public string? TotpSecret { get; set; }
    public bool TotpEnabled { get; set; }
    
    // WebAuthn credentials
    public ICollection<WebAuthnCredential> WebAuthnCredentials { get; set; } = new List<WebAuthnCredential>();
}

public class WebAuthnCredential
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    
    // Multi-tenancy
    public Guid TenantId { get; set; }
    
    // User association
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    
    // WebAuthn credential data
    [MaxLength(256)]
    public string CredentialId { get; set; } = string.Empty; // Base64URL encoded credential ID
    [MaxLength(1024)]
    public string PublicKey { get; set; } = string.Empty; // Base64 encoded public key
    [MaxLength(100)]
    public string Type { get; set; } = "public-key"; // Credential type
    [MaxLength(100)]
    public string? AttestationType { get; set; } // Attestation type (none, self, indirect, direct)
    [MaxLength(256)]
    public string? AaguidBase64 { get; set; } // Authenticator AAGUID as Base64
    public uint SignatureCounter { get; set; } // Signature counter for replay protection
    [MaxLength(500)]
    public string? Transport { get; set; } // JSON array of transports (usb, nfc, ble, internal)
    
    // User-friendly metadata
    [MaxLength(200)]
    public string? FriendlyName { get; set; } // User-assigned name for the credential
    [MaxLength(100)]
    public string? DeviceType { get; set; } // Device type hint (security-key, platform, cross-platform)
    
    // Management
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Realm
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty; // slug, e.g., "admin"
    [MaxLength(200)]
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool AllowUnconfirmedLogin { get; set; } = true;
}

public class Role
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;
    public Guid RealmId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Scope
{
    [Key]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty; // e.g., openid, profile, email, offline_access, roles
    
    // Multi-tenancy support: NULL for global scopes (e.g., openid, profile)
    public Guid? TenantId { get; set; }
    
    // IsGlobal = true for standard OAuth2/OIDC scopes that are shared across all tenants
    public bool IsGlobal { get; set; } = false;
    
    [MaxLength(200)]
    public string? Description { get; set; }
    public bool IsExposed { get; set; } = true;
}

public class Client
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public string ClientId { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public bool RequirePkce { get; set; } = true;
    public bool RequireConsent { get; set; } = true;
    [MaxLength(500)]
    [Obsolete("Use ClientSecrets collection instead. Retained for backward compatibility during migration.")]
    public string? ClientSecretHash { get; set; } // null => public client
    public Guid RealmId { get; set; } // parent realm (now required)
    [MaxLength(2000)]
    public string? IntrospectionAudiencesJson { get; set; } // optional per-client allow-list
    // New: public keys for validating signed client artifacts (private_key_jwt, JAR)
    [MaxLength(8000)]
    public string? PublicJwksJson { get; set; }
    [MaxLength(2000)]
    public string? PublicJwksUri { get; set; }

    // New: policy knobs moved from appsettings to per-client storage
    public bool RequirePar { get; set; } = false;
    [MaxLength(2000)]
    public string? IntrospectionResponseFieldsJson { get; set; }
    [MaxLength(2000)]
    public string? IntrospectionMtlsThumbprintsJson { get; set; }

    // New: per-client redirect URI allow-lists
    [MaxLength(4000)]
    public string? AllowedLoginRedirectUrisJson { get; set; }
    [MaxLength(4000)]
    public string? AllowedLogoutRedirectUrisJson { get; set; }

    // New: login methods configuration
    public bool AllowLocalLogin { get; set; } = true;
    public bool AllowExternalIdp { get; set; } = true;
    public bool AllowQrLogin { get; set; } = false;

    // New: UI style scheme key for login pages (null => default)
    [MaxLength(50)]
    public string? LoginStyleKey { get; set; }

    // New: M2M (client_credentials) policy
    // Per-client allow-list of audiences for CC tokens; if set, overrides global audiences.
    [MaxLength(2000)]
    public string? M2MAllowedAudiencesJson { get; set; }
    // Per-client access token lifetime for CC (seconds). Null or <=0 => default 900s.
    public int? M2MAccessTokenLifetimeSeconds { get; set; }
    // Allowed token endpoint auth methods for this client (CC):
    public bool AllowClientSecretBasic { get; set; } = true;
    public bool AllowClientSecretPost { get; set; } = true;
    public bool AllowPrivateKeyJwt { get; set; } = true;
    // Optional mTLS requirement for CC: list of allowed certificate thumbprints (case-insensitive). Empty => no mTLS required.
    [MaxLength(2000)]
    public string? M2MMtlsThumbprintsJson { get; set; }

    // New: external provisioning/linking policy
    public bool AllowExternalAutoProvision { get; set; } = true; // if false, external users must pre-exist or be linked
    public bool AllowExternalEmailLinking { get; set; } = true;   // allow linking by email when ExternalIdentity missing
    public bool RequireEmailLinkConfirmation { get; set; } = true; // if true, show confirmation UI instead of auto-linking

    // New: Front-channel logout configuration
    [MaxLength(2000)]
    public string? FrontChannelLogoutUri { get; set; }
    public bool FrontChannelLogoutSessionRequired { get; set; } = true;

    // New: Back-channel logout configuration
    [MaxLength(2000)]
    public string? BackChannelLogoutUri { get; set; }
    public bool BackChannelLogoutSessionRequired { get; set; } = true;

    // New: OBO/Token Exchange policy (nullable => not enforced / defaults)
    public bool? OboEnabled { get; set; }
    [MaxLength(2000)]
    public string? OboAllowedSourceAudiencesJson { get; set; }
    [MaxLength(2000)]
    public string? OboAllowedTargetAudiencesJson { get; set; }
    [MaxLength(2000)]
    public string? OboAllowedScopesJson { get; set; }
    public int? OboMaxDelegationDepth { get; set; }
    public int? OboMaxLifetimeMinutes { get; set; }
    public OboDpopMode? OboDpopMode { get; set; }
    [MaxLength(2000)]
    public string? OboAllowedCallersJson { get; set; }

    // New: Auto-approval for new user registrations
    /// <summary>
    /// Controls whether new user registrations from this client are automatically approved.
    /// No = manual approval required (default), OnlyExternalIdp = auto-approve external IdP logins only, All = auto-approve all registrations.
    /// </summary>
    public AutoApprovalMode AutoApprovalMode { get; set; } = AutoApprovalMode.No;

    public bool AutoAssignNewUsersToClient { get; set; } = false;

    // Navigation properties
    public List<ClientSecret> ClientSecrets { get; set; } = new();
}

public class ClientSecret
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid ClientId { get; set; }          // FK to Client.Id
    public Client Client { get; set; } = null!; // Navigation property
    
    [MaxLength(500)]
    public string SecretHash { get; set; } = string.Empty; // Argon2id/BCrypt hash
    
    [MaxLength(100)]
    public string? Description { get; set; }    // User-friendly label ("Production secret Q4 2025")
    
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAtUtc { get; set; }  // null => not yet active
    public DateTime? ExpiresAtUtc { get; set; }    // null => no expiry
    public DateTime? RevokedAtUtc { get; set; }    // null => not revoked
    
    public bool IsPrimary { get; set; } = false;   // Only one primary per client (recommended for new usage)
    
    // Audit fields
    [MaxLength(200)]
    public string? CreatedBy { get; set; }         // Username/subject who created
    [MaxLength(200)]
    public string? ActivatedBy { get; set; }
    [MaxLength(200)]
    public string? RevokedBy { get; set; }
    
    // Usage tracking (optional)
    public DateTime? LastUsedAtUtc { get; set; }
    public long UsageCount { get; set; } = 0;
}

public class ClientScope
{
    public Guid ClientId { get; set; }
    [MaxLength(100)]
    public string ScopeName { get; set; } = string.Empty;
}

public class UserAlternativeEmail
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid UserId { get; set; }
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(256)]
    public string NormalizedEmail { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
}

public static class EmailConfirmationPurposes
{
    public const string Primary = "primary";
    public const string Alternative = "alternative";
}

public class EmailConfirmation
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid? UserAlternativeEmailId { get; set; }
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(100)]
    public string TokenHash { get; set; } = string.Empty;
    [MaxLength(50)]
    public string Purpose { get; set; } = EmailConfirmationPurposes.Primary;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    [MaxLength(2000)]
    public string? MetadataJson { get; set; }
}

public class UserClientAssignment
{
    public Guid UserId { get; set; }
    public Guid ClientId { get; set; }
    public Guid RealmId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserRoleAssignment
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid ClientId { get; set; }
    public Guid RealmId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserRealmRoleAssignment
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid RealmId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class UserClientRoleAssignment
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Guid ClientId { get; set; }
    public bool IsActive { get; set; } = true;
}

public class SigningKey
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy: nullable for backward compat, but should always be set in multi-tenant mode
    public Guid? TenantId { get; set; }

    public string Kid { get; set; } = string.Empty;
    public string Alg { get; set; } = "RS256";
    public string JwkJson { get; set; } = string.Empty; // private JWK material
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetiredAt { get; set; }
}

public class AuthorizationCode
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public string Code { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty; // public client id string
    public Guid UserId { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public string? Nonce { get; set; }
    public string? CodeChallenge { get; set; }
    [MaxLength(10)]
    public string? CodeChallengeMethod { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

public class Consent
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class Token
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(20)]
    public string Type { get; set; } = "refresh"; // refresh | access (opaque)
    [MaxLength(200)]
    public string TokenHash { get; set; } = string.Empty; // SHA-256 of token
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    [MaxLength(200)]
    public string? Audience { get; set; } // for opaque access tokens
    [MaxLength(64)]
    public string? Jti { get; set; }
    [MaxLength(200)]
    public string? CnfJkt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
    // OBO tracking (for opaque access tokens)
    public string? ActJson { get; set; }
    public int DelegationDepth { get; set; } = 0;
    // Session metadata (Phase 5B Feature 3)
    [MaxLength(100)]
    public string? IpAddress { get; set; }
    [MaxLength(500)]
    public string? UserAgent { get; set; }
}

[Microsoft.EntityFrameworkCore.Index(nameof(RevocationAudit.TokenHash), nameof(RevocationAudit.ClientId))]
public class RevocationAudit
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    [Required]
    public string ClientId { get; set; } = string.Empty;
    [MaxLength(200)]
    public string TokenHash { get; set; } = string.Empty;
    [MaxLength(20)]
    public string? TokenType { get; set; }
    [MaxLength(100)]
    public string? IpAddress { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PushedAuthorizationRequest
{
    public Guid Id { get; set; } = GuidHelper.NewId(); // opaque identifier

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(512)]
    public string? RequestUri { get; set; } // optional absolute request URI returned to client
    public string ClientId { get; set; } = string.Empty;
    public string RequestJson { get; set; } = string.Empty; // serialized AuthorizeRequest
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

public class Registration
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(256)]
    public string Email { get; set; } = string.Empty; // mandatory
    [MaxLength(256)]
    public string NormalizedEmail { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? FirstName { get; set; }
    [MaxLength(100)]
    public string? LastName { get; set; }
    public Guid? ClientId { get; set; }
    [MaxLength(500)]
    public string? PasswordHash { get; set; }
    // pending | approved | rejected
    [MaxLength(20)]
    public string State { get; set; } = "pending";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTimeOffset? RejectedAt { get; set; }
    public Guid? RejectedByUserId { get; set; }

    // New: Tenant creation fields for anonymous tenant admin registration
    public bool IsTenantAdmin { get; set; } = false;
    [MaxLength(100)]
    public string? TenantSlug { get; set; }
    [MaxLength(200)]
    public string? TenantName { get; set; }
    [MaxLength(500)]
    public string? TenantDescription { get; set; }
}

// New: IdP chaining entities
public enum IdentityProviderType
{
    Oidc = 0,
    Saml = 1
}

public class IdentityProvider
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(150)]
    public string Name { get; set; } = string.Empty; // unique key
    [MaxLength(200)]
    public string? DisplayName { get; set; }
    public IdentityProviderType Type { get; set; } = IdentityProviderType.Oidc;
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; } = false;
    /// <summary>
    /// When true, this IdP appears on the public registration page allowing users to register via external authentication.
    /// Only applicable for IdPs in the default tenant.
    /// </summary>
    public bool AllowRegistration { get; set; } = false;
    [MaxLength(2000)]
    public string? LogoUrl { get; set; }
    public int SortOrder { get; set; } = 0;
    [MaxLength(8000)]
    public string? ConfigJson { get; set; } // provider-specific config (OIDC now)
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClientIdentityProvider
{
    public Guid ClientId { get; set; }
    public Guid IdentityProviderId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsDefaultForClient { get; set; } = false;
    public bool AutoRedirectIfSingle { get; set; } = false;
    [MaxLength(100)]
    public string? RequiredAcr { get; set; }
    public int Order { get; set; } = 0;
}

public class IdentityProviderClaimMapping
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid IdentityProviderId { get; set; }
    [MaxLength(200)]
    public string ExternalClaim { get; set; } = string.Empty;
    [MaxLength(200)]
    public string LocalClaim { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? Transform { get; set; }
    public int Order { get; set; } = 0;
}

public enum IdentityProviderKeyPurpose
{
    Signing = 0,
    Encryption = 1
}

public class IdentityProviderKey
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid IdentityProviderId { get; set; }
    public IdentityProviderKeyPurpose Purpose { get; set; } = IdentityProviderKeyPurpose.Signing;
    [MaxLength(8000)]
    public string Jwk { get; set; } = string.Empty;
    [MaxLength(20)]
    public string Alg { get; set; } = "RS256";
    public bool Active { get; set; } = true;
    // New: whether this active key is eligible for public JWKS publication (provider JWKS endpoint)
    public bool Publishable { get; set; } = false;
    [MaxLength(200)]
    public string? Kid { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class ClientJwksHistory
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    public Guid ClientId { get; set; }
    [MaxLength(8000)]
    public string JwksJson { get; set; } = string.Empty;
    [MaxLength(50)]
    public string? Source { get; set; } // e.g., fetch|manual|restore
    [MaxLength(64)]
    public string? Hash { get; set; } // SHA-256 hex of compacted JSON
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

// OBO policy DPoP bridging modes
public enum OboDpopMode
{
    Deny = 0,
    RequireSameJkt = 1,
    AllowSameJktOnly = 2
}

public class ExternalIdentity
{
    public Guid Id { get; set; } = GuidHelper.NewId();
    [MaxLength(2000)] public string Issuer { get; set; } = string.Empty; // upstream iss
    [MaxLength(400)] public string Subject { get; set; } = string.Empty; // upstream sub
    public Guid UserId { get; set; }
    [MaxLength(150)] public string? ProviderName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(4000)] public string? ClaimsJson { get; set; }
}

// New: Outbox entity for back-channel logout delivery
public class BackchannelLogoutNotification
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    public Guid ClientDbId { get; set; } // link to Clients table
    public string ClientId { get; set; } = string.Empty; // stable client_id string
    [MaxLength(2000)] public string TargetUri { get; set; } = string.Empty;
    [MaxLength(8000)] public string LogoutToken { get; set; } = string.Empty; // compact JWT
    [MaxLength(64)] public string? Sid { get; set; }
    [MaxLength(200)] public string? Sub { get; set; }
    // pending | in_progress | succeeded | failed | dead_letter
    [MaxLength(20)] public string Status { get; set; } = "pending";
    public int AttemptCount { get; set; } = 0;
    public int MaxAttempts { get; set; } = 5;
    public int? LastHttpStatus { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
}

// New: Opaque logout redirect reference entity
public class LogoutRedirectReference
{
    [Key]
    [MaxLength(64)]
    public string Id { get; set; } = string.Empty; // random base64url (>=96 bits entropy)
    [MaxLength(200)] public string ClientId { get; set; } = string.Empty; // client that initiated logout
    [MaxLength(2000)] public string RedirectUri { get; set; } = string.Empty; // validated original post_logout_redirect_uri
    [MaxLength(400)] public string? State { get; set; } // optional state to echo back
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddMinutes(5);
    public bool Used { get; set; } = false; // single-use guard
}

// New: QR code login session entity
public class QrLoginSession
{
    public Guid Id { get; set; } = GuidHelper.NewId();

    // Multi-tenancy
    public Guid TenantId { get; set; }

    [MaxLength(128)]
    public string SessionToken { get; set; } = string.Empty; // unique, indexed
    [MaxLength(64)]
    public string? SessionTokenHash { get; set; } // SHA256 hash for lookup
    [MaxLength(200)]
    public string ClientId { get; set; } = string.Empty;
    [MaxLength(2000)]
    public string ReturnUrl { get; set; } = string.Empty;
    [MaxLength(128)]
    public string CodeChallenge { get; set; } = string.Empty;
    [MaxLength(10)]
    public string CodeChallengeMethod { get; set; } = "S256";
    [MaxLength(1000)]
    public string State { get; set; } = string.Empty;
    [MaxLength(200)]
    public string? Nonce { get; set; }
    [MaxLength(1000)]
    public string Scope { get; set; } = string.Empty;
    public QrSessionStatus Status { get; set; } = QrSessionStatus.Pending;
    public Guid? UserId { get; set; }
    [MaxLength(200)]
    public string? AuthorizationCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ScannedAt { get; set; }
    public DateTimeOffset? AuthenticatedAt { get; set; }
    [MaxLength(500)]
    public string? MobileUserAgent { get; set; }
    [MaxLength(100)]
    public string? MobileIpAddress { get; set; }
}
