using Microsoft.EntityFrameworkCore;
using MrWhoOidc.KeyGen.Domain.Models;

namespace MrWhoOidc.KeyGen.Persistence;

/// <summary>
/// Database context for the Key and License Generation service.
/// </summary>
public class KeyGenDbContext : DbContext
{
    public KeyGenDbContext(DbContextOptions<KeyGenDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Key pair metadata records.
    /// </summary>
    public DbSet<KeyPairMetadata> KeyPairMetadata { get; set; } = null!;

    /// <summary>
    /// Key download audit records.
    /// </summary>
    public DbSet<KeyDownloadRecord> KeyDownloadRecords { get; set; } = null!;

    /// <summary>
    /// License token metadata records.
    /// </summary>
    public DbSet<LicenseTokenMetadata> LicenseTokenMetadata { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Configure KeyPairMetadata
        modelBuilder.Entity<KeyPairMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Kid)
                .IsRequired()
                .HasMaxLength(100);

            entity.HasIndex(e => e.Kid)
                .IsUnique();

            entity.Property(e => e.Algorithm)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(e => e.KeyType)
                .IsRequired()
                .HasMaxLength(10);

            entity.Property(e => e.Curve)
                .HasMaxLength(20);

            entity.Property(e => e.PublicKeyJwks)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .IsRequired();

            entity.HasIndex(e => e.CreatedAt);

            entity.Property(e => e.Status)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue("Active");

            entity.HasIndex(e => e.Status);

            entity.Property(e => e.CreatedBy)
                .HasMaxLength(200);

            entity.Property(e => e.DownloadCount)
                .IsRequired()
                .HasDefaultValue(0);

            // Relationship with KeyDownloadRecord
            entity.HasMany(e => e.DownloadRecords)
                .WithOne(e => e.KeyPairMetadata)
                .HasForeignKey(e => e.KeyPairMetadataId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Configure KeyDownloadRecord
        modelBuilder.Entity<KeyDownloadRecord>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.KeyPairMetadataId)
                .IsRequired();

            entity.HasIndex(e => e.KeyPairMetadataId);

            entity.Property(e => e.DownloadType)
                .IsRequired()
                .HasMaxLength(20);

            entity.Property(e => e.DownloadedAt)
                .IsRequired();

            entity.HasIndex(e => e.DownloadedAt);

            entity.Property(e => e.DownloadedBy)
                .HasMaxLength(200);

            entity.Property(e => e.IpAddress)
                .HasMaxLength(50);

            entity.Property(e => e.UserAgent)
                .HasMaxLength(500);
        });

        // Configure LicenseTokenMetadata
        modelBuilder.Entity<LicenseTokenMetadata>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.TokenId)
                .IsRequired()
                .HasMaxLength(100);

            entity.HasIndex(e => e.TokenId)
                .IsUnique();

            entity.Property(e => e.Tier)
                .IsRequired()
                .HasMaxLength(50);

            entity.HasIndex(e => e.Tier);

            entity.Property(e => e.Organization)
                .HasMaxLength(200);

            entity.Property(e => e.ValidFrom)
                .IsRequired();

            entity.Property(e => e.ValidUntil)
                .IsRequired();

            entity.Property(e => e.GeneratedAt)
                .IsRequired();

            entity.HasIndex(e => e.GeneratedAt);

            entity.Property(e => e.GeneratedBy)
                .HasMaxLength(200);
        });
    }
}
