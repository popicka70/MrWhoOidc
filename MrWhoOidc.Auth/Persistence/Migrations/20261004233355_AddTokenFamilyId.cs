using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTokenFamilyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                table: "Tokens",
                type: "uuid",
                nullable: true);

            // Backfill refresh tokens. ReplacedById on a rotated token points at its PARENT, so the family of a
            // token is the root of that chain: walk down from every root (no parent, or a parent that no longer
            // exists) and give each descendant the root's id. Anything not reached (e.g. a corrupt cycle) becomes
            // its own family. Access tokens are left NULL: existing rows cannot be tied to a grant reliably.
            // Instances that are still running the previous version keep writing NULL during a rolling deploy;
            // RevocationService falls back to the ReplacedById walk for those rows.
            migrationBuilder.Sql("""
                WITH RECURSIVE lineage AS (
                    SELECT t."Id", t."Id" AS family_id
                    FROM "Tokens" t
                    WHERE t."Type" = 'refresh'
                      AND (t."ReplacedById" IS NULL
                           OR NOT EXISTS (SELECT 1 FROM "Tokens" p WHERE p."Id" = t."ReplacedById" AND p."Type" = 'refresh'))
                    UNION
                    SELECT c."Id", l.family_id
                    FROM "Tokens" c
                    JOIN lineage l ON c."ReplacedById" = l."Id"
                    WHERE c."Type" = 'refresh'
                )
                UPDATE "Tokens" t
                SET "FamilyId" = l.family_id
                FROM lineage l
                WHERE t."Id" = l."Id" AND t."FamilyId" IS NULL;
                """);

            migrationBuilder.Sql("""
                UPDATE "Tokens"
                SET "FamilyId" = "Id"
                WHERE "Type" = 'refresh' AND "FamilyId" IS NULL;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_TenantId_FamilyId",
                table: "Tokens",
                columns: new[] { "TenantId", "FamilyId" },
                filter: "\"FamilyId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tokens_TenantId_FamilyId",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                table: "Tokens");
        }
    }
}
