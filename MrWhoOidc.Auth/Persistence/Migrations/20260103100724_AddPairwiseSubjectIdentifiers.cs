using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPairwiseSubjectIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SectorIdentifierUri",
                table: "Clients",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                table: "Clients",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "public");

            migrationBuilder.CreateTable(
                name: "PairwiseSubjectIdentifiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SectorIdentifier = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PairwiseSubjectIdentifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PairwiseSubjectIdentifiers_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PairwiseSubjectIdentifiers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PairwiseSubjectIdentifiers_TenantId",
                table: "PairwiseSubjectIdentifiers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PairwiseSubjectIdentifiers_UserId",
                table: "PairwiseSubjectIdentifiers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "UX_PairwiseSubjectIdentifiers_Tenant_Subject",
                table: "PairwiseSubjectIdentifiers",
                columns: new[] { "TenantId", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_PairwiseSubjectIdentifiers_Tenant_User_Sector",
                table: "PairwiseSubjectIdentifiers",
                columns: new[] { "TenantId", "UserId", "SectorIdentifier" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PairwiseSubjectIdentifiers");

            migrationBuilder.DropColumn(
                name: "SectorIdentifierUri",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                table: "Clients");
        }
    }
}
