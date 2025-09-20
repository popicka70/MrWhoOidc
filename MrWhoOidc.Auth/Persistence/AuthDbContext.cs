using Microsoft.EntityFrameworkCore;

namespace MrWhoOidc.Auth.Persistence;

public class AuthDbContext(DbContextOptions<AuthDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).IsRequired().HasMaxLength(200);
            b.HasIndex(x => x.Username).IsUnique();
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
    }
}

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty; // Argon2id
    public string? PasswordSalt { get; set; }
    public string HashAlgorithm { get; set; } = "argon2id";
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
