using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

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
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
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
                .OnDelete(DeleteBehavior.SetNull);
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

        // Optional explicit mapping for DataProtectionKeys (matches provider defaults)
        modelBuilder.Entity<DataProtectionKey>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.FriendlyName).HasMaxLength(200);
            b.Property(x => x.Xml).IsRequired();
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
