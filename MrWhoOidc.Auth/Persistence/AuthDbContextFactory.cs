using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MrWhoOidc.Auth.Persistence;

public class AuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args)
    {
        // Use an environment override if provided, otherwise a sensible local default
        var cs = Environment.GetEnvironmentVariable("AUTHDB_MIGRATIONS_CS")
             ?? "Host=localhost;Port=5432;Database=authdb;Username=postgres;Password=postgres";

        var optionsBuilder = new DbContextOptionsBuilder<AuthDbContext>()
            .UseNpgsql(cs);

        return new AuthDbContext(optionsBuilder.Options);
    }
}
