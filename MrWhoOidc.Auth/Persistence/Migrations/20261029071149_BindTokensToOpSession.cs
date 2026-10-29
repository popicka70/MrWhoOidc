using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BindTokensToOpSession : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "Tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "DeviceCodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "CibaAuthenticationRequests",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_SessionId",
                table: "Tokens",
                column: "SessionId",
                filter: "\"SessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCodes_SessionId",
                table: "DeviceCodes",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_CibaAuthenticationRequests_SessionId",
                table: "CibaAuthenticationRequests",
                column: "SessionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tokens_OpSessions_SessionId",
                table: "Tokens",
                column: "SessionId",
                principalTable: "OpSessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tokens_OpSessions_SessionId",
                table: "Tokens");

            migrationBuilder.DropIndex(
                name: "IX_Tokens_SessionId",
                table: "Tokens");

            migrationBuilder.DropIndex(
                name: "IX_DeviceCodes_SessionId",
                table: "DeviceCodes");

            migrationBuilder.DropIndex(
                name: "IX_CibaAuthenticationRequests_SessionId",
                table: "CibaAuthenticationRequests");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "DeviceCodes");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "CibaAuthenticationRequests");
        }
    }
}
