# pgAdmin (Aspire AppHost)

The Aspire AppHost (`MrWhoOidc.AppHost/AppHost.cs`) adds pgAdmin to its PostgreSQL resource:

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .WithPgAdmin();
var authDb = postgres.AddDatabase("authdb");
```

pgAdmin is only part of the AppHost. Neither `docker-compose.yml` nor `docker-compose.dev.yml` includes it; for those stacks use `docker compose exec postgres psql -U oidc -d authdb`.

## Open pgAdmin

```sh
dotnet run --project MrWhoOidc.AppHost
```

The Aspire dashboard opens from the AppHost launch profile. Open the endpoint of the **pgadmin** resource shown there (its host port is assigned by Aspire). Aspire registers the `postgres` server in pgAdmin; the generated `postgres` password and connection string are shown on the `postgres` resource in the dashboard. WebAuth applies EF Core migrations to `authdb` at startup.

## Useful Queries

```sql
-- Applied migrations
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";

-- Users and clients
SELECT "Id", "TenantId", "Username", "Email", "EmailVerified", "CreatedAt" FROM "Users";
SELECT "Id", "TenantId", "ClientId", "ClientName", "TokenEndpointAuthMethod", "RequirePkce" FROM "Clients";

-- Live tokens by type
SELECT "Type", COUNT(*) FROM "Tokens"
WHERE "RevokedAt" IS NULL AND "ExpiresAt" > NOW()
GROUP BY "Type";

-- Unexpired authorization codes (Code holds a hash, not the raw code)
SELECT "Id", "TenantId", "ClientId", "UserId", "Sid", "UpstreamIdp", "ExpiresAt", "Consumed"
FROM "AuthorizationCodes" WHERE "ExpiresAt" > NOW() ORDER BY "Id" DESC LIMIT 10;

-- Back-channel logout queue (Status: pending, succeeded, dead_letter)
SELECT "Id", "TenantId", "ClientId", "TargetUri", "Status", "AttemptCount", "LastHttpStatus", "NextAttemptAt"
FROM "BackchannelLogoutNotifications"
WHERE "Status" <> 'succeeded'
ORDER BY "NextAttemptAt";
```

The `GET /health/backchannel` endpoint reports the same pending backlog without database access.

## Troubleshooting

- **Cannot connect:** check that the `postgres` resource is running and use the port and password shown in the dashboard.
- **`authdb` missing:** confirm the `mrwhooidc-webauth` resource started; it creates the schema through migrations.
- **pgAdmin not starting:** check the resource logs in the dashboard.

## Security

pgAdmin is for local development only. Do not expose it beyond localhost, and do not point it at production databases with stored credentials.
