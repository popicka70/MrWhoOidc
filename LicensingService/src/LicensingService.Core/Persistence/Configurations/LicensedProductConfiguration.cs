using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensingService.Core.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the LicensedProduct entity.
/// </summary>
public class LicensedProductConfiguration : IEntityTypeConfiguration<LicensedProduct>
{
    public void Configure(EntityTypeBuilder<LicensedProduct> builder)
    {
        builder.ToTable("LicensedProducts");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.Identifier)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(p => p.DisplayName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(p => p.Description)
            .HasMaxLength(1000);

        builder.Property(p => p.Status)
            .IsRequired()
            .HasMaxLength(20)
            .HasDefaultValue("Active");

        builder.Property(p => p.CreatedAt)
            .IsRequired();

        // Indexes
        builder.HasIndex(p => p.Identifier)
            .IsUnique();

        builder.HasIndex(p => p.Status);

        // Relationships
        builder.HasMany(p => p.OptionDefinitions)
            .WithOne(o => o.Product)
            .HasForeignKey(o => o.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.Licenses)
            .WithOne(l => l.Product)
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
