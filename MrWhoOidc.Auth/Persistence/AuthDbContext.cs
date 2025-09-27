using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography; // added for future cryptographic helpers if needed

namespace MrWhoOidc.Auth.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
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

    // IDataProtectionKeyContext requirement
    public DbSet<DataProtectionKey> DataProtectionKeys { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).IsRequired().HasMaxLength(200);
            b.Property(x => x.Email).HasMaxLength(256);
            b.Property(x => x.Name).HasMaxLength(200);
            b.HasIndex(x => x.Username).IsUnique();
            b.HasIndex(x => x.Email).IsUnique();
            b.HasMany(x => x.AlternativeEmails)
                .WithOne()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // TOTP
            b.Property(x => x.TotpSecret).HasMaxLength(200);
        });

        modelBuilder.Entity<Realm>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(200);
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
        });

        // New: Scope catalog
        modelBuilder.Entity<Scope>(b =>
        {
            b.HasKey(x => x.Name);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Description).HasMaxLength(200);
        });

        modelBuilder.Entity<Client>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.ClientId).IsUnique();
            b.Property(x => x.ClientSecretHash).HasMaxLength(500);
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

            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
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

        // New: Alternative emails
        modelBuilder.Entity<UserAlternativeEmail>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Email).IsRequired().HasMaxLength(256);
            b.HasIndex(x => new { x.UserId, x.Email }).IsUnique();
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
            b.Property(x => x.FirstName).HasMaxLength(100);
            b.Property(x => x.LastName).HasMaxLength(100);
            b.Property(x => x.PasswordHash).HasMaxLength(500);
            b.Property(x => x.State).IsRequired().HasMaxLength(20);
            b.Property(x => x.CreatedAt).IsRequired();
            b.HasIndex(x => x.Email);
            b.HasOne<Client>()
                .WithMany()
                .HasForeignKey(x => x.ClientId)
                .OnDelete(DeleteBehavior.SetNull); // restore original behavior
        });

        modelBuilder.Entity<SigningKey>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Kid).IsRequired();
            b.HasIndex(x => x.Kid).IsUnique();
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
        });

        modelBuilder.Entity<Consent>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired();
            b.HasIndex(x => new { x.UserId, x.ClientId }).IsUnique();
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
            b.HasIndex(x => new { x.UserId, x.ClientId, x.Type });
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

        // Optional explicit mapping for DataProtectionKeys (matches provider defaults)
        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.FriendlyName).HasMaxLength(200);
            b.Property(x => x.Xml).IsRequired();
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
    }
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)]
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty; // Argon2id
    public string? PasswordSalt { get; set; }
    public string HashAlgorithm { get; set; } = "argon2id";
    [MaxLength(256)]
    public string? Email { get; set; }
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
}

public class Realm
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty; // slug, e.g., "admin"
    [MaxLength(200)]
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
    [MaxLength(200)]
    public string? Description { get; set; }
    public bool IsExposed { get; set; } = true;
}

public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)]
    public string ClientId { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public bool RequirePkce { get; set; } = true;
    public bool RequireConsent { get; set; } = true;
    [MaxLength(500)]
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
}

public class ClientScope
{
    public Guid ClientId { get; set; }
    [MaxLength(100)]
    public string ScopeName { get; set; } = string.Empty;
}

public class UserAlternativeEmail
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;
    public bool IsVerified { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
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
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kid { get; set; } = string.Empty;
    public string Alg { get; set; } = "RS256";
    public string JwkJson { get; set; } = string.Empty; // private JWK material
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RetiredAt { get; set; }
}

public class AuthorizationCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public class Token
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
}

[Microsoft.EntityFrameworkCore.Index(nameof(RevocationAudit.TokenHash), nameof(RevocationAudit.ClientId))]
public class RevocationAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid(); // opaque identifier
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
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty; // mandatory
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
}

// New: IdP chaining entities
public enum IdentityProviderType
{
    Oidc = 0,
    Saml = 1
}

public class IdentityProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(150)]
    public string Name { get; set; } = string.Empty; // unique key
    [MaxLength(200)]
    public string? DisplayName { get; set; }
    public IdentityProviderType Type { get; set; } = IdentityProviderType.Oidc;
    public bool Enabled { get; set; } = true;
    public bool IsDefault { get; set; } = false;
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
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid();
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
    public Guid Id { get; set; } = Guid.NewGuid();
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
