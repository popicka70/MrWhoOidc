using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDelegatedAccessGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DelegatedAccessGrants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegatorUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegateUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CapabilitiesJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    ResourceConstraintsJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptanceExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeclinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UseCount = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegatedAccessGrants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DelegatedAccessGrants_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DelegatedAccessGrants_UserAccounts_DelegatorUserAccountId",
                        column: x => x.DelegatorUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DelegatedAccessGrants_UserAccounts_DelegateUserAccountId",
                        column: x => x.DelegateUserAccountId,
                        principalTable: "UserAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DelegatedAccessInvitationTokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DelegatedAccessInvitationTokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DelegatedAccessInvitationTokens_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DelegatedAccessInvitationTokens_DelegatedAccessGrants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "DelegatedAccessGrants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessGrants_TenantId_DelegatorUserAccountId_Status_ExpiresAt",
                table: "DelegatedAccessGrants",
                columns: new[] { "TenantId", "DelegatorUserAccountId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessGrants_TenantId_DelegateUserAccountId_Status_ExpiresAt",
                table: "DelegatedAccessGrants",
                columns: new[] { "TenantId", "DelegateUserAccountId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessGrants_Status_ExpiresAt",
                table: "DelegatedAccessGrants",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessInvitationTokens_TokenHash",
                table: "DelegatedAccessInvitationTokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessInvitationTokens_GrantId_ConsumedAt_RevokedAt",
                table: "DelegatedAccessInvitationTokens",
                columns: new[] { "GrantId", "ConsumedAt", "RevokedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DelegatedAccessInvitationTokens");

            migrationBuilder.DropTable(
                name: "DelegatedAccessGrants");
        }
    }
}
