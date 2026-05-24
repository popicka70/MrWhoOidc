using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantDomainClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantDomainClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Domain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    NormalizedDomain = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EnrollmentMode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    VerificationToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    VerificationDnsName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    VerificationDnsValue = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUsername = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SettingsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDomainClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantDomainClaims_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantDomainClaims_NormalizedDomain",
                table: "TenantDomainClaims",
                column: "NormalizedDomain",
                unique: true,
                filter: "\"Status\" <> 'Revoked'");

            migrationBuilder.CreateIndex(
                name: "IX_TenantDomainClaims_Status",
                table: "TenantDomainClaims",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TenantDomainClaims_TenantId_NormalizedDomain",
                table: "TenantDomainClaims",
                columns: new[] { "TenantId", "NormalizedDomain" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantDomainClaims");
        }
    }
}
