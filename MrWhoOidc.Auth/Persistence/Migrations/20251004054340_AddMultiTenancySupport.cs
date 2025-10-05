using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MrWhoOidc.Auth.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiTenancySupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Users",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "SigningKeys",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Roles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Registrations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Realms",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "QrLoginSessions",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "PushedAuthorizationRequests",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "IdentityProviders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Consents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "Clients",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "BackchannelLogoutNotifications",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AuthorizationCodes",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IssuerUri = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SuspendedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LogoUrl = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PrimaryColor = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AccentColor = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    SettingsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    MaxUsers = table.Column<int>(type: "integer", nullable: false),
                    MaxClients = table.Column<int>(type: "integer", nullable: false),
                    MaxIdentityProviders = table.Column<int>(type: "integer", nullable: false),
                    AdminEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BillingPlan = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TrialEndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MetadataJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            // Seed default tenant for backward compatibility
            // This tenant will be used in single-tenant mode and as the default in multi-tenant mode
            var defaultTenantId = new Guid("00000000-0000-0000-0000-000000000001");
            migrationBuilder.InsertData(
                table: "Tenants",
                columns: new[] { "Id", "Slug", "Name", "Description", "IssuerUri", "Status", "CreatedAt", 
                    "SuspendedAt", "DeletedAt", "LogoUrl", "PrimaryColor", "AccentColor", "SettingsJson",
                    "MaxUsers", "MaxClients", "MaxIdentityProviders", "AdminEmail", "BillingPlan", 
                    "TrialEndsAt", "MetadataJson" },
                values: new object[] { 
                    defaultTenantId, 
                    "default", 
                    "Default Tenant", 
                    "Default tenant for single-tenant mode and existing data migration", 
                    "https://localhost:5001", // Will be overridden at runtime based on mode
                    1, // TenantStatus.Active
                    DateTimeOffset.UtcNow,
                    null, // SuspendedAt
                    null, // DeletedAt
                    null, // LogoUrl
                    null, // PrimaryColor
                    null, // AccentColor
                    null, // SettingsJson
                    10000, // MaxUsers
                    100, // MaxClients
                    10, // MaxIdentityProviders
                    null, // AdminEmail
                    "Enterprise", // BillingPlan
                    null, // TrialEndsAt
                    null // MetadataJson
                });

            // Update all existing records to reference the default tenant
            // This ensures existing data continues to work after migration
            migrationBuilder.Sql($"UPDATE \"Users\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Tokens\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Roles\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Registrations\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Realms\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"QrLoginSessions\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"PushedAuthorizationRequests\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"IdentityProviders\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Consents\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"Clients\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"BackchannelLogoutNotifications\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"AuthorizationCodes\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" = '00000000-0000-0000-0000-000000000000'");
            migrationBuilder.Sql($"UPDATE \"SigningKeys\" SET \"TenantId\" = '{defaultTenantId}' WHERE \"TenantId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId",
                table: "Users",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_TenantId",
                table: "Tokens",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_TenantId",
                table: "SigningKeys",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Roles_TenantId",
                table: "Roles",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_TenantId",
                table: "Registrations",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Realms_TenantId",
                table: "Realms",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_TenantId",
                table: "QrLoginSessions",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_PushedAuthorizationRequests_TenantId",
                table: "PushedAuthorizationRequests",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_TenantId",
                table: "IdentityProviders",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Consents_TenantId",
                table: "Consents",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Clients_TenantId",
                table: "Clients",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_BackchannelLogoutNotifications_TenantId",
                table: "BackchannelLogoutNotifications",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationCodes_TenantId",
                table: "AuthorizationCodes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Status",
                table: "Tenants",
                column: "Status");

            migrationBuilder.AddForeignKey(
                name: "FK_AuthorizationCodes_Tenants_TenantId",
                table: "AuthorizationCodes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BackchannelLogoutNotifications_Tenants_TenantId",
                table: "BackchannelLogoutNotifications",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Clients_Tenants_TenantId",
                table: "Clients",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Consents_Tenants_TenantId",
                table: "Consents",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_IdentityProviders_Tenants_TenantId",
                table: "IdentityProviders",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PushedAuthorizationRequests_Tenants_TenantId",
                table: "PushedAuthorizationRequests",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_QrLoginSessions_Tenants_TenantId",
                table: "QrLoginSessions",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Realms_Tenants_TenantId",
                table: "Realms",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Registrations_Tenants_TenantId",
                table: "Registrations",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Roles_Tenants_TenantId",
                table: "Roles",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SigningKeys_Tenants_TenantId",
                table: "SigningKeys",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tokens_Tenants_TenantId",
                table: "Tokens",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuthorizationCodes_Tenants_TenantId",
                table: "AuthorizationCodes");

            migrationBuilder.DropForeignKey(
                name: "FK_BackchannelLogoutNotifications_Tenants_TenantId",
                table: "BackchannelLogoutNotifications");

            migrationBuilder.DropForeignKey(
                name: "FK_Clients_Tenants_TenantId",
                table: "Clients");

            migrationBuilder.DropForeignKey(
                name: "FK_Consents_Tenants_TenantId",
                table: "Consents");

            migrationBuilder.DropForeignKey(
                name: "FK_IdentityProviders_Tenants_TenantId",
                table: "IdentityProviders");

            migrationBuilder.DropForeignKey(
                name: "FK_PushedAuthorizationRequests_Tenants_TenantId",
                table: "PushedAuthorizationRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_QrLoginSessions_Tenants_TenantId",
                table: "QrLoginSessions");

            migrationBuilder.DropForeignKey(
                name: "FK_Realms_Tenants_TenantId",
                table: "Realms");

            migrationBuilder.DropForeignKey(
                name: "FK_Registrations_Tenants_TenantId",
                table: "Registrations");

            migrationBuilder.DropForeignKey(
                name: "FK_Roles_Tenants_TenantId",
                table: "Roles");

            migrationBuilder.DropForeignKey(
                name: "FK_SigningKeys_Tenants_TenantId",
                table: "SigningKeys");

            migrationBuilder.DropForeignKey(
                name: "FK_Tokens_Tenants_TenantId",
                table: "Tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropIndex(
                name: "IX_Users_TenantId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Tokens_TenantId",
                table: "Tokens");

            migrationBuilder.DropIndex(
                name: "IX_SigningKeys_TenantId",
                table: "SigningKeys");

            migrationBuilder.DropIndex(
                name: "IX_Roles_TenantId",
                table: "Roles");

            migrationBuilder.DropIndex(
                name: "IX_Registrations_TenantId",
                table: "Registrations");

            migrationBuilder.DropIndex(
                name: "IX_Realms_TenantId",
                table: "Realms");

            migrationBuilder.DropIndex(
                name: "IX_QrLoginSessions_TenantId",
                table: "QrLoginSessions");

            migrationBuilder.DropIndex(
                name: "IX_PushedAuthorizationRequests_TenantId",
                table: "PushedAuthorizationRequests");

            migrationBuilder.DropIndex(
                name: "IX_IdentityProviders_TenantId",
                table: "IdentityProviders");

            migrationBuilder.DropIndex(
                name: "IX_Consents_TenantId",
                table: "Consents");

            migrationBuilder.DropIndex(
                name: "IX_Clients_TenantId",
                table: "Clients");

            migrationBuilder.DropIndex(
                name: "IX_BackchannelLogoutNotifications_TenantId",
                table: "BackchannelLogoutNotifications");

            migrationBuilder.DropIndex(
                name: "IX_AuthorizationCodes_TenantId",
                table: "AuthorizationCodes");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "SigningKeys");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Roles");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Registrations");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Realms");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "QrLoginSessions");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "PushedAuthorizationRequests");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "IdentityProviders");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Consents");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "Clients");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "BackchannelLogoutNotifications");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AuthorizationCodes");
        }
    }
}
