using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LicensingService.Core.Persistence.Configurations;

/// <summary>
/// EF Core configuration for the ProductOptionDefinition entity.
/// </summary>
public class ProductOptionDefinitionConfiguration : IEntityTypeConfiguration<ProductOptionDefinition>
{
    public void Configure(EntityTypeBuilder<ProductOptionDefinition> builder)
    {
        builder.ToTable("ProductOptionDefinitions");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.OptionKey)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(o => o.DisplayName)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(o => o.DataType)
            .IsRequired()
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(o => o.DefaultValue)
            .HasMaxLength(200);

        builder.Property(o => o.Description)
            .HasMaxLength(500);

        builder.Property(o => o.SortOrder)
            .HasDefaultValue(0);

        // Indexes
        builder.HasIndex(o => new { o.ProductId, o.OptionKey })
            .IsUnique();

        builder.HasIndex(o => o.ProductId);
    }
}
