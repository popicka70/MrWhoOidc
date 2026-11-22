using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUserImpersonationSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserImpersonationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUserAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetUsername = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TargetDisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetSecurityStamp = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TargetOpSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TicketReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedByUserAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedFromIpHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorAcr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ActorAmrJson = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ActorIdp = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserImpersonationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserImpersonationSessions_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserImpersonationSessions_ActorUserAccountId_Status_Expires~",
                table: "UserImpersonationSessions",
                columns: new[] { "ActorUserAccountId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserImpersonationSessions_Status_ExpiresAt",
                table: "UserImpersonationSessions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UserImpersonationSessions_TargetOpSessionId",
                table: "UserImpersonationSessions",
                column: "TargetOpSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserImpersonationSessions_TenantId_Status_ExpiresAt",
                table: "UserImpersonationSessions",
                columns: new[] { "TenantId", "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserImpersonationSessions");
        }
    }
}
