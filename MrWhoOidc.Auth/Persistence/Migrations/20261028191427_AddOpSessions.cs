using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOpSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OpSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthTime = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IdleExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Acr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AmrJson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpstreamIdp = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    IpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OpSessionClients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OpSessionClients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OpSessionClients_OpSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "OpSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OpSessionClients_SessionId",
                table: "OpSessionClients",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_OpSessionClients_SessionId_ClientId",
                table: "OpSessionClients",
                columns: new[] { "SessionId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OpSessionClients_TenantId",
                table: "OpSessionClients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpSessions_ExpiresAt",
                table: "OpSessions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_OpSessions_TenantId",
                table: "OpSessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_OpSessions_UserId",
                table: "OpSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OpSessionClients");

            migrationBuilder.DropTable(
                name: "OpSessions");
        }
    }
}
