using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Persistence.Configurations;

internal sealed class LicenseHistoryEntryConfiguration : IEntityTypeConfiguration<LicenseHistoryEntry>
{
    public void Configure(EntityTypeBuilder<LicenseHistoryEntry> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Action)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.OldLicenseKey)
            .HasMaxLength(2000);

        builder.Property(x => x.NewLicenseKey)
            .HasMaxLength(2000);

        builder.Property(x => x.OldTier)
            .HasMaxLength(50);

        builder.Property(x => x.NewTier)
            .HasMaxLength(50);

        builder.Property(x => x.Notes)
            .HasMaxLength(1000);

        builder.Property(x => x.Reason)
            .HasMaxLength(500);

        builder.Property(x => x.UserAgent)
            .HasMaxLength(200);

        builder.Property(x => x.IpAddress)
            .HasMaxLength(45);

        builder.HasIndex(x => x.LicenseId);
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => new { x.LicenseId, x.CreatedAt });

        builder.HasOne(x => x.License)
            .WithMany(x => x.History)
            .HasForeignKey(x => x.LicenseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.CreatedByUser)
            .WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
