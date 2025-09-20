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
            b.HasIndex(x => x.Email);
        });

        modelBuilder.Entity<Realm>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).IsRequired().HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(200);
        });

        modelBuilder.Entity<Client>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.ClientId).IsUnique();
            b.Property(x => x.ClientSecretHash).HasMaxLength(500);
            b.Property(x => x.RealmId).IsRequired();
            b.HasIndex(x => x.RealmId);
            b.HasOne<Realm>()
                .WithMany()
                .HasForeignKey(x => x.RealmId)
                .OnDelete(DeleteBehavior.Cascade);
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
    [MaxLength(200)]
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
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
    public string Type { get; set; } = "refresh"; // refresh
    [MaxLength(200)]
    public string TokenHash { get; set; } = string.Empty; // SHA-256 of token
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
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
