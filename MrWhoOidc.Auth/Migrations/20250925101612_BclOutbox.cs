using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class BclOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackchannelLogoutNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientDbId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    TargetUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    LogoutToken = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Sid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Sub = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 5),
                    LastHttpStatus = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackchannelLogoutNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BackchannelLogoutNotifications_Clients_ClientDbId",
                        column: x => x.ClientDbId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BackchannelLogoutNotifications_ClientDbId",
                table: "BackchannelLogoutNotifications",
                column: "ClientDbId");

            migrationBuilder.CreateIndex(
                name: "IX_BackchannelLogoutNotifications_ClientId",
                table: "BackchannelLogoutNotifications",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_BackchannelLogoutNotifications_Status_NextAttemptAt",
                table: "BackchannelLogoutNotifications",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackchannelLogoutNotifications");
        }
    }
}
