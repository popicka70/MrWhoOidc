using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MrWhoOidc.Auth.Licensing.Entities;

namespace MrWhoOidc.Auth.Persistence.Configurations;

internal sealed class LicenseLimitConfiguration : IEntityTypeConfiguration<LicenseLimit>
{
    public void Configure(EntityTypeBuilder<LicenseLimit> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Tier)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.LimitType)
            .IsRequired()
            .HasMaxLength(100);

        builder.HasIndex(x => new { x.Tier, x.LimitType, x.IsActive })
            .IsUnique()
            .HasFilter("\"IsActive\" = true");
    }
}
