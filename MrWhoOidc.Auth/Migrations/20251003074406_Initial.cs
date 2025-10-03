using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace MrWhoOidc.Auth.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuthorizationCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RedirectUri = table.Column<string>(type: "text", nullable: false),
                    ScopesJson = table.Column<string>(type: "text", nullable: false),
                    Nonce = table.Column<string>(type: "text", nullable: true),
                    CodeChallenge = table.Column<string>(type: "text", nullable: true),
                    CodeChallengeMethod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuthorizationCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Consents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    ScopesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Consents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Xml = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LogoUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    ConfigJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LogoutRedirectReferences",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RedirectUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Used = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LogoutRedirectReferences", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PushedAuthorizationRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestUri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PushedAuthorizationRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "QrLoginSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionToken = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SessionTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReturnUrl = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CodeChallenge = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CodeChallengeMethod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    State = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Nonce = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Scope = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AuthorizationCode = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AuthenticatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MobileUserAgent = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    MobileIpAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QrLoginSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Realms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Realms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RevocationAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TokenType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RevocationAudits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Scopes",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsExposed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Scopes", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "SigningKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kid = table.Column<string>(type: "text", nullable: false),
                    Alg = table.Column<string>(type: "text", nullable: false),
                    JwkJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SigningKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "text", nullable: false),
                    ScopesJson = table.Column<string>(type: "text", nullable: false),
                    Audience = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Jti = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CnfJkt = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ActJson = table.Column<string>(type: "text", nullable: true),
                    DelegationDepth = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    PasswordSalt = table.Column<string>(type: "text", nullable: true),
                    HashAlgorithm = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailVerified = table.Column<bool>(type: "boolean", nullable: false),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TotpSecret = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TotpEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviderClaimMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    LocalClaim = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Transform = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviderClaimMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviderClaimMappings_IdentityProviders_IdentityPro~",
                        column: x => x.IdentityProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IdentityProviderKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<int>(type: "integer", nullable: false),
                    Jwk = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Alg = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    Publishable = table.Column<bool>(type: "boolean", nullable: false),
                    Kid = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityProviderKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IdentityProviderKeys_IdentityProviders_IdentityProviderId",
                        column: x => x.IdentityProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ClientName = table.Column<string>(type: "text", nullable: true),
                    RequirePkce = table.Column<bool>(type: "boolean", nullable: false),
                    RequireConsent = table.Column<bool>(type: "boolean", nullable: false),
                    ClientSecretHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    IntrospectionAudiencesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PublicJwksJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    PublicJwksUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequirePar = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    IntrospectionResponseFieldsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IntrospectionMtlsThumbprintsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AllowedLoginRedirectUrisJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AllowedLogoutRedirectUrisJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AllowLocalLogin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowExternalIdp = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowQrLogin = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LoginStyleKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    M2MAllowedAudiencesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    M2MAccessTokenLifetimeSeconds = table.Column<int>(type: "integer", nullable: true),
                    AllowClientSecretBasic = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowClientSecretPost = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowPrivateKeyJwt = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    M2MMtlsThumbprintsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AllowExternalAutoProvision = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    AllowExternalEmailLinking = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    RequireEmailLinkConfirmation = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    FrontChannelLogoutUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FrontChannelLogoutSessionRequired = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    BackChannelLogoutUri = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BackChannelLogoutSessionRequired = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    OboEnabled = table.Column<bool>(type: "boolean", nullable: true),
                    OboAllowedSourceAudiencesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OboAllowedTargetAudiencesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OboAllowedScopesJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OboMaxDelegationDepth = table.Column<int>(type: "integer", nullable: true),
                    OboMaxLifetimeMinutes = table.Column<int>(type: "integer", nullable: true),
                    OboDpopMode = table.Column<int>(type: "integer", nullable: true),
                    OboAllowedCallersJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AutoApprovalMode = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clients_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Roles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Roles_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExternalIdentities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Issuer = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProviderName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimsJson = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalIdentities", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExternalIdentities_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserAlternativeEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsVerified = table.Column<bool>(type: "boolean", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAlternativeEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAlternativeEmails_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

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

            migrationBuilder.CreateTable(
                name: "ClientIdentityProviders",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdentityProviderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDefaultForClient = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    AutoRedirectIfSingle = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    RequiredAcr = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientIdentityProviders", x => new { x.ClientId, x.IdentityProviderId });
                    table.ForeignKey(
                        name: "FK_ClientIdentityProviders_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientIdentityProviders_IdentityProviders_IdentityProviderId",
                        column: x => x.IdentityProviderId,
                        principalTable: "IdentityProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientJwksHistories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    JwksJson = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientJwksHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientJwksHistories_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientScopes",
                columns: table => new
                {
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientScopes", x => new { x.ClientId, x.ScopeName });
                    table.ForeignKey(
                        name: "FK_ClientScopes_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClientScopes_Scopes_ScopeName",
                        column: x => x.ScopeName,
                        principalTable: "Scopes",
                        principalColumn: "Name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FirstName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LastName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Registrations_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "UserClientAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClientAssignments", x => new { x.UserId, x.ClientId, x.RealmId });
                    table.ForeignKey(
                        name: "FK_UserClientAssignments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserClientAssignments_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserClientAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserClientRoleAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserClientRoleAssignments", x => new { x.UserId, x.RoleId, x.ClientId });
                    table.ForeignKey(
                        name: "FK_UserClientRoleAssignments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserClientRoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserClientRoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRealmRoleAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRealmRoleAssignments", x => new { x.UserId, x.RoleId, x.RealmId });
                    table.ForeignKey(
                        name: "FK_UserRealmRoleAssignments_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRealmRoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRealmRoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserRoleAssignments",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    RealmId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleAssignments", x => new { x.UserId, x.RoleId, x.ClientId, x.RealmId });
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Clients_ClientId",
                        column: x => x.ClientId,
                        principalTable: "Clients",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Realms_RealmId",
                        column: x => x.RealmId,
                        principalTable: "Realms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Roles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "Roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserRoleAssignments_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuthorizationCodes_Code",
                table: "AuthorizationCodes",
                column: "Code",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "IX_ClientIdentityProviders_IdentityProviderId",
                table: "ClientIdentityProviders",
                column: "IdentityProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientJwksHistories_ClientId_CreatedAt",
                table: "ClientJwksHistories",
                columns: new[] { "ClientId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_ClientId",
                table: "Clients",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Clients_RealmId",
                table: "Clients",
                column: "RealmId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientScopes_ScopeName",
                table: "ClientScopes",
                column: "ScopeName");

            migrationBuilder.CreateIndex(
                name: "IX_Consents_UserId_ClientId",
                table: "Consents",
                columns: new[] { "UserId", "ClientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_Issuer_Subject",
                table: "ExternalIdentities",
                columns: new[] { "Issuer", "Subject" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalIdentities_UserId",
                table: "ExternalIdentities",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderClaimMappings_IdentityProviderId",
                table: "IdentityProviderClaimMappings",
                column: "IdentityProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviderKeys_Provider_Kid_CI",
                table: "IdentityProviderKeys",
                columns: new[] { "IdentityProviderId", "Kid" });

            migrationBuilder.CreateIndex(
                name: "IX_IdentityProviders_Name",
                table: "IdentityProviders",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LogoutRedirectReferences_ExpiresAt",
                table: "LogoutRedirectReferences",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_PushedAuthorizationRequests_ExpiresAt",
                table: "PushedAuthorizationRequests",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionToken",
                table: "QrLoginSessions",
                column: "SessionToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_SessionTokenHash",
                table: "QrLoginSessions",
                column: "SessionTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_QrLoginSessions_Status_ExpiresAt",
                table: "QrLoginSessions",
                columns: new[] { "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Realms_Name",
                table: "Realms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_ClientId",
                table: "Registrations",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_Registrations_NormalizedEmail",
                table: "Registrations",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_RevocationAudits_TokenHash_ClientId",
                table: "RevocationAudits",
                columns: new[] { "TokenHash", "ClientId" });

            migrationBuilder.CreateIndex(
                name: "IX_Roles_RealmId_Name",
                table: "Roles",
                columns: new[] { "RealmId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SigningKeys_Kid",
                table: "SigningKeys",
                column: "Kid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_TokenHash",
                table: "Tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tokens_UserId_ClientId_Type",
                table: "Tokens",
                columns: new[] { "UserId", "ClientId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_UserAlternativeEmails_NormalizedEmail",
                table: "UserAlternativeEmails",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAlternativeEmails_UserId_NormalizedEmail",
                table: "UserAlternativeEmails",
                columns: new[] { "UserId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserClientAssignments_ClientId",
                table: "UserClientAssignments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserClientAssignments_RealmId",
                table: "UserClientAssignments",
                column: "RealmId");

            migrationBuilder.CreateIndex(
                name: "IX_UserClientRoleAssignments_ClientId",
                table: "UserClientRoleAssignments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserClientRoleAssignments_RoleId",
                table: "UserClientRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRealmRoleAssignments_RealmId",
                table: "UserRealmRoleAssignments",
                column: "RealmId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRealmRoleAssignments_RoleId",
                table: "UserRealmRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_ClientId",
                table: "UserRoleAssignments",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_RealmId",
                table: "UserRoleAssignments",
                column: "RealmId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_RoleId",
                table: "UserRoleAssignments",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuthorizationCodes");

            migrationBuilder.DropTable(
                name: "BackchannelLogoutNotifications");

            migrationBuilder.DropTable(
                name: "ClientIdentityProviders");

            migrationBuilder.DropTable(
                name: "ClientJwksHistories");

            migrationBuilder.DropTable(
                name: "ClientScopes");

            migrationBuilder.DropTable(
                name: "Consents");

            migrationBuilder.DropTable(
                name: "DataProtectionKeys");

            migrationBuilder.DropTable(
                name: "ExternalIdentities");

            migrationBuilder.DropTable(
                name: "IdentityProviderClaimMappings");

            migrationBuilder.DropTable(
                name: "IdentityProviderKeys");

            migrationBuilder.DropTable(
                name: "LogoutRedirectReferences");

            migrationBuilder.DropTable(
                name: "PushedAuthorizationRequests");

            migrationBuilder.DropTable(
                name: "QrLoginSessions");

            migrationBuilder.DropTable(
                name: "Registrations");

            migrationBuilder.DropTable(
                name: "RevocationAudits");

            migrationBuilder.DropTable(
                name: "SigningKeys");

            migrationBuilder.DropTable(
                name: "Tokens");

            migrationBuilder.DropTable(
                name: "UserAlternativeEmails");

            migrationBuilder.DropTable(
                name: "UserClientAssignments");

            migrationBuilder.DropTable(
                name: "UserClientRoleAssignments");

            migrationBuilder.DropTable(
                name: "UserRealmRoleAssignments");

            migrationBuilder.DropTable(
                name: "UserRoleAssignments");

            migrationBuilder.DropTable(
                name: "Scopes");

            migrationBuilder.DropTable(
                name: "IdentityProviders");

            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropTable(
                name: "Roles");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Realms");
        }
    }
}
