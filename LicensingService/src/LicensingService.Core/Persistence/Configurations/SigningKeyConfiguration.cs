using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensingService.Core.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the SigningKey entity.
/// </summary>
public class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> builder)
    {
        builder.ToTable("SigningKeys");

        builder.HasKey(k => k.Id);

        builder.Property(k => k.Kid)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(k => k.Algorithm)
            .IsRequired()
            .HasMaxLength(20);

        builder.Property(k => k.PublicKeyJwks)
            .IsRequired()
            .HasColumnType("TEXT"); // JWK JSON stored as TEXT

        builder.Property(k => k.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(k => k.CreatedAt)
            .IsRequired();

        // Indexes
        builder.HasIndex(k => k.Kid)
            .IsUnique();

        builder.HasIndex(k => k.Status);
    }
}
