using LicensingService.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace LicensingService.Core.Persistence;

/// <summary>
/// Database context for the Licensing Service.
/// Supports both SQLite (development) and PostgreSQL (production).
/// </summary>
public class LicensingDbContext : DbContext
{
    public LicensingDbContext(DbContextOptions<LicensingDbContext> options)
        : base(options)
    {
    }

    // DbSet properties
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<LicensedProduct> Products => Set<LicensedProduct>();
    public DbSet<ProductOptionDefinition> ProductOptionDefinitions => Set<ProductOptionDefinition>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<LicenseEvent> LicenseEvents => Set<LicenseEvent>();
    public DbSet<SigningKey> SigningKeys => Set<SigningKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all entity configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LicensingDbContext).Assembly);
    }
}
