using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindDelegatedAccessGrantToClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "DelegatedAccessGrants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessGrants_ClientId",
                table: "DelegatedAccessGrants",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_DelegatedAccessGrants_TenantId_ClientId_Status_ExpiresAt",
                table: "DelegatedAccessGrants",
                columns: new[] { "TenantId", "ClientId", "Status", "ExpiresAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_DelegatedAccessGrants_Clients_ClientId",
                table: "DelegatedAccessGrants",
                column: "ClientId",
                principalTable: "Clients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DelegatedAccessGrants_Clients_ClientId",
                table: "DelegatedAccessGrants");

            migrationBuilder.DropIndex(
                name: "IX_DelegatedAccessGrants_ClientId",
                table: "DelegatedAccessGrants");

            migrationBuilder.DropIndex(
                name: "IX_DelegatedAccessGrants_TenantId_ClientId_Status_ExpiresAt",
                table: "DelegatedAccessGrants");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "DelegatedAccessGrants");
        }
    }
}
