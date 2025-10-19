using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIcon : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantIconId",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantIcons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FileData = table.Column<byte[]>(type: "bytea", nullable: false),
                    FileSize = table.Column<long>(type: "bigint", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantIcons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantIcons_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_TenantIconId",
                table: "Tenants",
                column: "TenantIconId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantIcons_TenantId",
                table: "TenantIcons",
                column: "TenantId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tenants_TenantIcons_TenantIconId",
                table: "Tenants",
                column: "TenantIconId",
                principalTable: "TenantIcons",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tenants_TenantIcons_TenantIconId",
                table: "Tenants");

            migrationBuilder.DropTable(
                name: "TenantIcons");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_TenantIconId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TenantIconId",
                table: "Tenants");
        }
    }
}
