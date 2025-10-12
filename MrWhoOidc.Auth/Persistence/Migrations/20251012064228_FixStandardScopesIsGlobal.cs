using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixStandardScopesIsGlobal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Update standard OIDC scopes to be global
            migrationBuilder.Sql(@"
                UPDATE ""Scopes""
                SET ""IsGlobal"" = true, ""TenantId"" = NULL
                WHERE ""Name"" IN ('openid', 'profile', 'email', 'address', 'phone', 'offline_access', 'roles')
                  AND ""IsGlobal"" = false;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reversing this would be problematic as we don't know which tenant they belonged to
            // Leave as no-op; re-seeding would be needed to restore original state
        }
    }
}
