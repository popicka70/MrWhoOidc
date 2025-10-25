using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Persistence.Configurations;

internal sealed class FeatureUsageMetricConfiguration : IEntityTypeConfiguration<FeatureUsageMetric>
{
    public void Configure(EntityTypeBuilder<FeatureUsageMetric> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.FeatureName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.AggregationDate)
            .HasColumnType("date");

        builder.HasIndex(x => new { x.TenantId, x.FeatureName, x.AggregationDate })
            .IsUnique();

        builder.HasIndex(x => x.LicenseId);
        builder.HasIndex(x => x.AggregationDate);

        builder.HasOne(x => x.License)
            .WithMany(x => x.UsageMetrics)
            .HasForeignKey(x => x.LicenseId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.Tenant)
            .WithMany()
            .HasForeignKey(x => x.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
