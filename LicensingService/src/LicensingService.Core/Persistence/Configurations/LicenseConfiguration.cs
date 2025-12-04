using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensingService.Core.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the License entity.
/// </summary>
public class LicenseConfiguration : IEntityTypeConfiguration<License>
{
    public void Configure(EntityTypeBuilder<License> builder)
    {
        builder.ToTable("Licenses");

        builder.HasKey(l => l.Id);

        builder.Property(l => l.TokenId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(l => l.SignedToken)
            .IsRequired();

        builder.Property(l => l.SigningKeyId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(l => l.Tier)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.Scope)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(l => l.ValidFrom)
            .IsRequired();

        builder.Property(l => l.ValidUntil)
            .IsRequired();

        builder.Property(l => l.Options)
            .HasColumnType("TEXT"); // JSON stored as TEXT in SQLite, jsonb in PostgreSQL

        builder.Property(l => l.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(l => l.CreatedAt)
            .IsRequired();

        builder.Property(l => l.CreatedBy)
            .HasMaxLength(200);

        builder.Property(l => l.RevokedBy)
            .HasMaxLength(200);

        builder.Property(l => l.RevocationReason)
            .HasMaxLength(500);

        // Indexes
        builder.HasIndex(l => l.TokenId)
            .IsUnique();

        builder.HasIndex(l => l.CustomerId);

        builder.HasIndex(l => l.ProductId);

        builder.HasIndex(l => l.Status);

        // Composite indexes for customer-first search
        builder.HasIndex(l => new { l.CustomerId, l.ProductId });

        builder.HasIndex(l => new { l.CustomerId, l.Status, l.ValidUntil });

        // Self-referencing relationship for parent/child licenses
        builder.HasOne(l => l.ParentLicense)
            .WithMany(l => l.ChildLicenses)
            .HasForeignKey(l => l.ParentLicenseId)
            .OnDelete(DeleteBehavior.Restrict);

        // Relationships
        builder.HasOne(l => l.Customer)
            .WithMany(c => c.Licenses)
            .HasForeignKey(l => l.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(l => l.Events)
            .WithOne(e => e.License)
            .HasForeignKey(e => e.LicenseId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
