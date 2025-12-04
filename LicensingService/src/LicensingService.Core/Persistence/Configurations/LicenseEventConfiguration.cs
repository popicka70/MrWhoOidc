using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensingService.Core.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the LicenseEvent entity.
/// </summary>
public class LicenseEventConfiguration : IEntityTypeConfiguration<LicenseEvent>
{
    public void Configure(EntityTypeBuilder<LicenseEvent> builder)
    {
        builder.ToTable("LicenseEvents");

        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasMaxLength(50)
            .HasConversion<string>();

        builder.Property(e => e.Timestamp)
            .IsRequired();

        builder.Property(e => e.Actor)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(e => e.Details)
            .HasColumnType("TEXT"); // JSON stored as TEXT

        // Indexes
        builder.HasIndex(e => e.LicenseId);

        builder.HasIndex(e => e.Timestamp);

        builder.HasIndex(e => new { e.LicenseId, e.Timestamp });
    }
}
