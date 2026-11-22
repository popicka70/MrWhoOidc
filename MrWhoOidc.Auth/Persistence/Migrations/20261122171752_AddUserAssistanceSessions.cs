using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAssistanceSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserAssistanceSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequesterDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    HelperUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    HelperUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    HelperUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HelperEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    RightsMode = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TicketReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SessionExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    EndReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    TargetOpSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetSecurityStamp = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorAcr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ActorAmrJson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorIdp = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    InvitationTokenHash = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedFromIpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveRoleIdsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAssistanceSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAssistanceSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserAssistanceSessions_HelperUserAccountId_Status",
                table: "UserAssistanceSessions",
                columns: new[] { "HelperUserAccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAssistanceSessions_InvitationTokenHash",
                table: "UserAssistanceSessions",
                column: "InvitationTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_UserAssistanceSessions_RequesterUserId_Status",
                table: "UserAssistanceSessions",
                columns: new[] { "RequesterUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAssistanceSessions_TargetOpSessionId",
                table: "UserAssistanceSessions",
                column: "TargetOpSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAssistanceSessions_TenantId_Status_SessionExpiresAt",
                table: "UserAssistanceSessions",
                columns: new[] { "TenantId", "Status", "SessionExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserAssistanceSessions");
        }
    }
}
