using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LinkUserToUserAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserAccountId",
                table: "Users",
                type: "uuid",
                nullable: true);

            // Backfill. Deliberately conservative: link only what is unambiguous and never by username
            // (the field the K1 takeover abused). Unlinked rows stay NULL and keep the legacy lookup.
            // 1. Home users: their account was created with the user's own id.
            migrationBuilder.Sql("""
                UPDATE "Users" u
                SET "UserAccountId" = u."Id"
                WHERE u."UserAccountId" IS NULL
                  AND EXISTS (SELECT 1 FROM "UserAccounts" a WHERE a."Id" = u."Id");
                """);

            // 2. Users in other tenants: same email AND the account is a member of that tenant, with exactly
            //    one candidate on both sides. lower() because older rows were normalised in upper case.
            migrationBuilder.Sql("""
                WITH candidates AS (
                    SELECT u."Id" AS user_id, u."TenantId" AS tenant_id, a."Id" AS account_id
                    FROM "Users" u
                    JOIN "UserAccounts" a ON lower(a."NormalizedEmail") = lower(u."NormalizedEmail")
                    JOIN "UserTenantMemberships" m ON m."UserAccountId" = a."Id" AND m."TenantId" = u."TenantId"
                    WHERE u."UserAccountId" IS NULL AND u."NormalizedEmail" IS NOT NULL
                ),
                unambiguous AS (
                    SELECT c.user_id, c.tenant_id, c.account_id
                    FROM candidates c
                    WHERE (SELECT count(*) FROM candidates c2 WHERE c2.user_id = c.user_id) = 1
                      AND (SELECT count(*) FROM candidates c3 WHERE c3.tenant_id = c.tenant_id AND c3.account_id = c.account_id) = 1
                      AND NOT EXISTS (SELECT 1 FROM "Users" l WHERE l."TenantId" = c.tenant_id AND l."UserAccountId" = c.account_id)
                )
                UPDATE "Users" u
                SET "UserAccountId" = x.account_id
                FROM unambiguous x
                WHERE u."Id" = x.user_id;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_UserAccountId",
                table: "Users",
                columns: new[] { "TenantId", "UserAccountId" },
                unique: true,
                filter: "\"UserAccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserAccountId",
                table: "Users",
                column: "UserAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_UserAccounts_UserAccountId",
                table: "Users",
                column: "UserAccountId",
                principalTable: "UserAccounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_UserAccounts_UserAccountId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId_UserAccountId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_UserAccountId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "UserAccountId",
                table: "Users");
        }
    }
}
