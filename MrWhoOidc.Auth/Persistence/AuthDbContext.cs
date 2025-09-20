using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;

namespace MrWhoOidc.Auth.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();
    public DbSet<AuthorizationCode> AuthorizationCodes => Set<AuthorizationCode>();
    public DbSet<Consent> Consents => Set<Consent>();
    public DbSet<Token> Tokens => Set<Token>();

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

        modelBuilder.Entity<Client>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ClientId).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.ClientId).IsUnique();
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
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty; // Argon2id
    public string? PasswordSalt { get; set; }
    public string HashAlgorithm { get; set; } = "argon2id";
    public string? Email { get; set; }
    public bool EmailVerified { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class Client
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClientId { get; set; } = string.Empty;
    public string? ClientName { get; set; }
    public bool RequirePkce { get; set; } = true;
    public bool RequireConsent { get; set; } = true;
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
    public string Code { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty; // public client id string
    public Guid UserId { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public string? Nonce { get; set; }
    public string? CodeChallenge { get; set; }
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
    public string Type { get; set; } = "refresh"; // refresh
    public string TokenHash { get; set; } = string.Empty; // SHA-256 of token
    public Guid UserId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? ReplacedById { get; set; }
}
