using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.KeyGen.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KeyPairMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kid = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Algorithm = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    KeyType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    KeySize = table.Column<int>(type: "INTEGER", nullable: true),
                    Curve = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    PublicKeyJwks = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, defaultValue: "Active"),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyPairMetadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LicenseTokenMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Tier = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Organization = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ValidFrom = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ValidUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Features = table.Column<string>(type: "TEXT", nullable: true),
                    Limits = table.Column<string>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    GeneratedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicenseTokenMetadata", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "KeyDownloadRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyPairMetadataId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DownloadType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DownloadedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DownloadedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyDownloadRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_KeyDownloadRecords_KeyPairMetadata_KeyPairMetadataId",
                        column: x => x.KeyPairMetadataId,
                        principalTable: "KeyPairMetadata",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeyDownloadRecords_DownloadedAt",
                table: "KeyDownloadRecords",
                column: "DownloadedAt");

            migrationBuilder.CreateIndex(
                name: "IX_KeyDownloadRecords_KeyPairMetadataId",
                table: "KeyDownloadRecords",
                column: "KeyPairMetadataId");

            migrationBuilder.CreateIndex(
                name: "IX_KeyPairMetadata_CreatedAt",
                table: "KeyPairMetadata",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_KeyPairMetadata_Kid",
                table: "KeyPairMetadata",
                column: "Kid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_KeyPairMetadata_Status",
                table: "KeyPairMetadata",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTokenMetadata_GeneratedAt",
                table: "LicenseTokenMetadata",
                column: "GeneratedAt");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTokenMetadata_Tier",
                table: "LicenseTokenMetadata",
                column: "Tier");

            migrationBuilder.CreateIndex(
                name: "IX_LicenseTokenMetadata_TokenId",
                table: "LicenseTokenMetadata",
                column: "TokenId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeyDownloadRecords");

            migrationBuilder.DropTable(
                name: "LicenseTokenMetadata");

            migrationBuilder.DropTable(
                name: "KeyPairMetadata");
        }
    }
}
