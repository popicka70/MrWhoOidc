using System.Text.Json;
using System.Text.Json.Serialization;
using MrWhoOidc.Cli.Commands;
using MrWhoOidc.Cli.Configuration;
using MrWhoOidc.Cli.Services;

namespace MrWhoOidc.Cli.Mcp;

/// <summary>
/// Registry of MCP tools that map to CLI commands.
/// Each tool exposes a CLI operation to LLMs via JSON-RPC.
/// After the user runs "mrwho-cli login" once, an AI agent can perform
/// all IdP administration autonomously through these tools.
/// </summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public McpToolRegistry()
    {
        RegisterStatusTools();
        RegisterRealmTools();
        RegisterClientTools();
        RegisterScopeTools();
        RegisterUserTools();
    }

    public McpTool[] GetAllTools()
    {
        return _tools.Values.Select(t => new McpTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = t.InputSchema
        }).ToArray();
    }

    public async Task<object[]> ExecuteToolAsync(string toolName, Dictionary<string, JsonElement>? arguments, CancellationToken ct)
    {
        if (!_tools.TryGetValue(toolName, out var tool))
            throw new KeyNotFoundException($"Unknown tool '{toolName}'. Call tools/list to see available tools.");

        return await tool.Handler(arguments ?? [], ct);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string GetString(Dictionary<string, JsonElement> args, string key, string? defaultValue = null)
    {
        if (args.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            return el.GetString() ?? defaultValue ?? string.Empty;
        return defaultValue ?? string.Empty;
    }

    private static bool GetBool(Dictionary<string, JsonElement> args, string key, bool defaultValue = false)
    {
        if (args.TryGetValue(key, out var el))
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
        }
        return defaultValue;
    }

    private static string[]? GetStringArray(Dictionary<string, JsonElement> args, string key)
    {
        if (!args.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return null;
        return el.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToArray();
    }

    private static object[] Text(string text) => [new { type = "text", text }];

    private static object[] Json(object value) =>
        [new { type = "text", text = JsonSerializer.Serialize(value, JsonOptions) }];

    private static object[] Error(string message) =>
        [new { type = "text", text = $"ERROR: {message}" }];

    private void RegisterTool(McpToolDefinition tool) => _tools[tool.Name] = tool;

    private static JsonElement CreateSchema(object properties, string[]? required = null)
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties,
            required = (object)(required ?? Array.Empty<string>())
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Status & guide tools
    // ──────────────────────────────────────────────────────────────────────────

    private void RegisterStatusTools()
    {
        RegisterTool(new McpToolDefinition
        {
            Name = "setup_guide",
            Description = "Return the complete MrWhoOidc IdP setup walkthrough. Call this first so you know the full workflow. The guide covers every step from server start to a working OIDC client. Only the login step (Step 1) requires a human; everything else is automatable via MCP tools.",
            InputSchema = CreateSchema(new { }),
            Handler = async (args, ct) =>
            {
                await Task.CompletedTask;
                return Text(SetupCommand.GetGuideText());
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "check_auth_status",
            Description = "Check whether the CLI has a saved authenticated profile. Does NOT make any network calls — reads local config only. Returns the current profile name, server URL, and whether a token is present. If not authenticated, instruct the user to run: mrwho-cli login --server https://HOST/t/TENANT",
            InputSchema = CreateSchema(new
            {
                profile = new { type = "string", description = "Profile name to check (optional; defaults to the current active profile)" }
            }),
            Handler = async (args, ct) =>
            {
                await Task.CompletedTask;
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var profileName = GetString(args, "profile");
                    ProfileConfig? profile = null;

                    if (!string.IsNullOrWhiteSpace(profileName))
                    {
                        config.Profiles.TryGetValue(profileName, out profile);
                    }
                    else
                    {
                        profile = config.GetCurrentProfile();
                        profileName = config.CurrentProfile;
                    }

                    if (profile is null)
                    {
                        return Text($"NOT AUTHENTICATED. No profile found. Run: mrwho-cli login --server https://HOST/t/TENANT");
                    }

                    var status = new
                    {
                        authenticated = profile.IsAuthenticated,
                        profileName,
                        serverUrl = profile.ServerUrl,
                        tenantSlug = profile.TenantSlug,
                        isPlatformAdmin = profile.IsPlatformAdmin,
                        hasAccessToken = !string.IsNullOrWhiteSpace(profile.AccessToken),
                        hasRefreshToken = !string.IsNullOrWhiteSpace(profile.RefreshToken),
                        note = profile.IsAuthenticated
                            ? "Profile is authenticated. Proceed with admin operations."
                            : "Profile exists but is NOT authenticated. Run: mrwho-cli login"
                    };

                    return Json(status);
                }
                catch (Exception ex)
                {
                    return Error($"Failed to read config: {ex.Message}");
                }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "server_health",
            Description = "Check health of all MrWhoOidc server subsystems (database, backchannel logout, client secrets, issuer config, etc.). Returns a list of subsystem statuses. Requires an active login profile.",
            InputSchema = CreateSchema(new
            {
                profile = new { type = "string", description = "Profile name (optional)" }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());

                    var endpoints = new[]
                    {
                        ("Database",           "health"),
                        ("Backchannel Logout", "health/backchannel"),
                        ("Client Secrets",     "health/client-secrets"),
                        ("Global Auth",        "health/global-auth"),
                        ("Issuer Config",      "health/issuer"),
                        ("Forwarded Headers",  "health/forwarded-headers"),
                    };

                    var results = new List<object>();
                    foreach (var (name, path) in endpoints)
                    {
                        try
                        {
                            var payload = await CliAdminApiClient.GetAsync<HealthPayloadMcp>(config, connection, path, ct);
                            results.Add(new { subsystem = name, status = payload?.Status ?? "unknown", description = payload?.Description });
                        }
                        catch (Exception ex)
                        {
                            results.Add(new { subsystem = name, status = "error", description = ex.Message });
                        }
                    }

                    return Json(new { healthChecks = results });
                }
                catch (Exception ex)
                {
                    return Error(ex.Message);
                }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "server_discovery",
            Description = "Fetch and return the OIDC discovery document (.well-known/openid-configuration) for the configured server. Returns all OIDC endpoints, supported grant types, scopes, and algorithms. Does not require authentication.",
            InputSchema = CreateSchema(new
            {
                server = new { type = "string", description = "Server URL including /t/<tenant-slug>, e.g. https://host/t/default. Defaults to the current profile's server." }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var serverUrl = GetString(args, "server").NullIfEmpty()
                        ?? CliServerConnection.ResolveServerUrlOrThrow(config);

                    var discoveryUrl = serverUrl.TrimEnd('/') + "/.well-known/openid-configuration";
                    using var http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
                    var json = await http.GetStringAsync(discoveryUrl, ct);
                    var doc = JsonSerializer.Deserialize<JsonElement>(json);
                    return Json(doc);
                }
                catch (Exception ex)
                {
                    return Error(ex.Message);
                }
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Realm tools
    // ──────────────────────────────────────────────────────────────────────────

    private void RegisterRealmTools()
    {
        RegisterTool(new McpToolDefinition
        {
            Name = "realm_list",
            Description = "List all realms in the current tenant. A realm groups clients, users, and roles. You need the realm ID (GUID) when creating clients. Always call this before client_create.",
            InputSchema = CreateSchema(new
            {
                profile = new { type = "string", description = "Profile name (optional)" }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());
                    var realms = await CliAdminApiClient.GetListAsync<JsonElement>(config, connection, "admin/api/realms", ct);
                    return Json(new { realms, hint = "Use the 'id' field as realmId when calling client_create." });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Client tools
    // ──────────────────────────────────────────────────────────────────────────

    private void RegisterClientTools()
    {
        RegisterTool(new McpToolDefinition
        {
            Name = "client_list",
            Description = "List OIDC clients in the current tenant. Returns clientId, clientName, realmName, requirePkce, requirePar, and internal ID. Use the internal ID (GUID) for client_get and client_validate.",
            InputSchema = CreateSchema(new
            {
                search = new { type = "string", description = "Filter by client ID or name (optional)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());
                    var search = GetString(args, "search").NullIfEmpty();
                    var path = string.IsNullOrEmpty(search) ? "admin/api/clients" : $"admin/api/clients?search={Uri.EscapeDataString(search)}";
                    var clients = await CliAdminApiClient.GetListAsync<JsonElement>(config, connection, path, ct);
                    return Json(new { clients });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "client_get",
            Description = "Get full details of a specific OIDC client by its internal ID (GUID). Returns all configuration fields including scopes, grant types, redirect URIs, and security settings.",
            InputSchema = CreateSchema(new
            {
                id = new { type = "string", description = "Client internal ID (GUID)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }, required: ["id"]),
            Handler = async (args, ct) =>
            {
                try
                {
                    var id = GetString(args, "id");
                    if (string.IsNullOrWhiteSpace(id)) return Error("'id' is required.");
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());
                    var client = await CliAdminApiClient.GetAsync<JsonElement?>(config, connection, $"admin/api/clients/{id}", ct);
                    if (client is null) return Text("Client not found.");
                    return Json(client);
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "client_create",
            Description = """
                Create a new OIDC client. Always call realm_list first to get the realmId.

                Common patterns:
                - Web app: grantTypes=["authorization_code"], requirePkce=true, provide redirectUris
                - M2M service: grantTypes=["client_credentials"], no redirectUris needed
                - Both flows: grantTypes=["authorization_code","client_credentials"]

                Set createSecret=true to generate an initial client secret — it will be returned
                in this response (only shown once; store it securely).

                After creation, call client_validate with the returned id to confirm configuration.
                """,
            InputSchema = CreateSchema(new
            {
                clientId = new { type = "string", description = "OAuth 2.0 client_id string (unique within tenant)" },
                clientName = new { type = "string", description = "Human-readable display name" },
                realmId = new { type = "string", description = "Realm GUID (get from realm_list)" },
                grantTypes = new { type = "array", items = new { type = "string" }, description = "e.g. [\"authorization_code\"], [\"client_credentials\"], or both" },
                scope = new { type = "string", description = "Space-separated allowed scopes, e.g. \"openid profile email api.read\"" },
                redirectUris = new { type = "array", items = new { type = "string" }, description = "Allowed login redirect URIs (required for authorization_code)" },
                logoutRedirectUris = new { type = "array", items = new { type = "string" }, description = "Allowed post-logout redirect URIs (optional)" },
                requirePkce = new { type = "boolean", description = "Require PKCE (recommended for public clients; default true)" },
                requireConsent = new { type = "boolean", description = "Require user consent screen (default true)" },
                createSecret = new { type = "boolean", description = "Generate and return an initial client secret (shown once)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }, required: ["clientId", "clientName", "realmId"]),
            Handler = async (args, ct) =>
            {
                try
                {
                    var clientId = GetString(args, "clientId");
                    var clientName = GetString(args, "clientName");
                    var realmId = GetString(args, "realmId");
                    if (string.IsNullOrWhiteSpace(clientId)) return Error("'clientId' is required.");
                    if (string.IsNullOrWhiteSpace(clientName)) return Error("'clientName' is required.");
                    if (string.IsNullOrWhiteSpace(realmId)) return Error("'realmId' is required.");

                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());

                    var grantTypes = GetStringArray(args, "grantTypes");
                    var redirectUris = GetStringArray(args, "redirectUris");
                    var logoutRedirectUris = GetStringArray(args, "logoutRedirectUris");
                    var scope = GetString(args, "scope").NullIfEmpty();
                    var requirePkce = args.ContainsKey("requirePkce") ? GetBool(args, "requirePkce", true) : (bool?)null;
                    var requireConsent = args.ContainsKey("requireConsent") ? GetBool(args, "requireConsent", true) : (bool?)null;
                    var createSecret = GetBool(args, "createSecret");

                    var result = await CliAdminApiClient.PostAsync<JsonElement?>(config, connection, "admin/api/clients", new
                    {
                        clientId,
                        clientName,
                        realmId = Guid.Parse(realmId),
                        requirePkce,
                        requireConsent,
                        scope,
                        grantTypes = grantTypes?.ToList(),
                        allowedLoginRedirectUris = redirectUris?.ToList(),
                        allowedLogoutRedirectUris = logoutRedirectUris?.ToList(),
                        createInitialSecret = createSecret
                    }, ct);

                    if (result is null) return Error("Server returned an empty response.");
                    return Json(new { client = result, hint = createSecret ? "The client secret is shown above. Store it securely — it cannot be retrieved again. Call client_validate with the returned id to confirm configuration." : "Call client_validate with the returned id to confirm configuration." });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "client_validate",
            Description = "Validate an OIDC client's configuration and report issues. Checks for: missing redirect URIs, missing secrets, misconfigured scopes, and other common problems. Always call this after client_create.",
            InputSchema = CreateSchema(new
            {
                id = new { type = "string", description = "Client internal ID (GUID)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }, required: ["id"]),
            Handler = async (args, ct) =>
            {
                try
                {
                    var id = GetString(args, "id");
                    if (string.IsNullOrWhiteSpace(id)) return Error("'id' is required.");
                    if (!Guid.TryParse(id, out var clientGuid)) return Error("'id' must be a valid GUID.");

                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());

                    // Gather data the same way ClientValidateCommand does
                    var client = await CliAdminApiClient.GetAsync<JsonElement?>(config, connection, $"admin/api/clients/{id}", ct);
                    if (client is null) return Text("Client not found.");

                    var secretsRaw = await CliAdminApiClient.GetAsync<JsonElement?>(config, connection, $"admin/api/clients/{id}/secrets", ct);
                    var scopesRaw = await CliAdminApiClient.GetAsync<JsonElement?>(config, connection, $"admin/api/clients/{id}/scopes", ct);

                    return Json(new
                    {
                        client,
                        secrets = secretsRaw,
                        scopes = scopesRaw,
                        hint = "Review the above data. Common issues: no active secrets (client_credentials flow fails), no redirect URIs (authorization_code flow fails), scopes not assigned."
                    });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Scope tools
    // ──────────────────────────────────────────────────────────────────────────

    private void RegisterScopeTools()
    {
        RegisterTool(new McpToolDefinition
        {
            Name = "scope_list",
            Description = "List OAuth/OIDC scopes available in the current tenant. Standard scopes (openid, profile, email) are always available. Custom API scopes appear here after creation.",
            InputSchema = CreateSchema(new
            {
                profile = new { type = "string", description = "Profile name (optional)" }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());
                    var scopes = await CliAdminApiClient.GetListAsync<JsonElement>(config, connection, "admin/api/scopes", ct);
                    return Json(new { scopes });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "scope_create",
            Description = "Create a new tenant-scoped OAuth/OIDC scope. Use this for custom API scopes (e.g. \"api.read\", \"reports.write\"). After creating, include the scope name in client_create's scope parameter.",
            InputSchema = CreateSchema(new
            {
                name = new { type = "string", description = "Scope identifier, e.g. \"api.read\"" },
                description = new { type = "string", description = "Human-readable description" },
                isExposed = new { type = "boolean", description = "Whether to expose this scope in discovery (default: true)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }, required: ["name"]),
            Handler = async (args, ct) =>
            {
                try
                {
                    var name = GetString(args, "name");
                    if (string.IsNullOrWhiteSpace(name)) return Error("'name' is required.");
                    var description = GetString(args, "description").NullIfEmpty();
                    var isExposed = args.ContainsKey("isExposed") ? GetBool(args, "isExposed", true) : (bool?)null;

                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());

                    await CliAdminApiClient.PostAsync<object?>(config, connection, "admin/api/scopes",
                        new { name, description, isExposed }, ct);

                    return Text($"Scope '{name}' created successfully. You can now reference it in client_create's scope parameter.");
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });
    }

    // ──────────────────────────────────────────────────────────────────────────
    // User tools
    // ──────────────────────────────────────────────────────────────────────────

    private void RegisterUserTools()
    {
        RegisterTool(new McpToolDefinition
        {
            Name = "user_list",
            Description = "List users in the current tenant. Returns user ID, username, email, name, and MFA status.",
            InputSchema = CreateSchema(new
            {
                search = new { type = "string", description = "Filter by username or email (optional)" },
                take = new { type = "integer", description = "Number of results to return (default: 20)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }),
            Handler = async (args, ct) =>
            {
                try
                {
                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());
                    var search = GetString(args, "search").NullIfEmpty();
                    var qp = new List<string>();
                    if (search != null) qp.Add($"search={Uri.EscapeDataString(search)}");
                    var path = qp.Count > 0 ? $"admin/api/users?{string.Join('&', qp)}" : "admin/api/users";
                    var page = await CliAdminApiClient.GetAsync<JsonElement?>(config, connection, path, ct);
                    return Json(page ?? (object)"No users found.");
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });

        RegisterTool(new McpToolDefinition
        {
            Name = "user_create",
            Description = "Create a new user in the current tenant. If password is omitted, a secure random password is generated. The password is returned in this response (shown once — store it securely).",
            InputSchema = CreateSchema(new
            {
                username = new { type = "string", description = "Unique username" },
                email = new { type = "string", description = "Email address (optional but recommended)" },
                name = new { type = "string", description = "Display name (optional)" },
                password = new { type = "string", description = "Password (optional; a secure random one is generated if omitted)" },
                profile = new { type = "string", description = "Profile name (optional)" }
            }, required: ["username"]),
            Handler = async (args, ct) =>
            {
                try
                {
                    var username = GetString(args, "username");
                    if (string.IsNullOrWhiteSpace(username)) return Error("'username' is required.");
                    var email = GetString(args, "email").NullIfEmpty();
                    var name = GetString(args, "name").NullIfEmpty();
                    var password = GetString(args, "password").NullIfEmpty();

                    var config = await CliConfig.LoadAsync(ct);
                    var connection = CliServerConnection.ResolveAuthenticatedConnectionOrThrow(
                        config, profileName: GetString(args, "profile").NullIfEmpty());

                    var result = await CliAdminApiClient.PostAsync<JsonElement?>(config, connection, "admin/api/users",
                        new { username, email, name, password }, ct);

                    if (result is null) return Error("Server returned an empty response.");
                    return Json(new
                    {
                        user = result,
                        warning = "The password above is shown once. Store it securely immediately."
                    });
                }
                catch (Exception ex) { return Error(ex.Message); }
            }
        });
    }
}

// Minimal health payload for MCP deserialization (avoids pulling in Spectre.Console dependency chain)
internal sealed class HealthPayloadMcp
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }
}

internal static class StringExtensions
{
    public static string? NullIfEmpty(this string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Internal definition of an MCP tool with execution handler.
/// </summary>
internal sealed class McpToolDefinition
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement InputSchema { get; init; }
    public required Func<Dictionary<string, JsonElement>, CancellationToken, Task<object[]>> Handler { get; init; }
}
