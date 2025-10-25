using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Persistence.Configurations;

internal sealed class LicenseConfiguration : IEntityTypeConfiguration<License>
{
    public void Configure(EntityTypeBuilder<License> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.LicenseKey)
            .IsRequired()
            .HasMaxLength(2000);

        builder.Property(x => x.Tier)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.OrganizationName)
            .HasMaxLength(500);

        builder.Property(x => x.RevocationReason)
            .HasMaxLength(500);

        builder.HasIndex(x => x.TenantId);
        builder.HasIndex(x => new { x.TenantId, x.IsActive });
        builder.HasIndex(x => x.ValidUntil);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.CreatedByUser)
            .WithMany()
            .HasForeignKey(x => x.CreatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.UpdatedByUser)
            .WithMany()
            .HasForeignKey(x => x.UpdatedBy)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasMany(x => x.History)
            .WithOne(x => x.License)
            .HasForeignKey(x => x.LicenseId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.UsageMetrics)
            .WithOne(x => x.License)
            .HasForeignKey(x => x.LicenseId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
