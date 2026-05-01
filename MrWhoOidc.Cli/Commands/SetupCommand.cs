using System.CommandLine;
using Spectre.Console;

namespace MrWhoOidc.Cli.Commands;

/// <summary>
/// Prints a comprehensive, LLM-friendly setup guide for configuring an MrWhoOidc IdP.
/// The only manual step is authenticating via device-code flow; everything else can be
/// driven by an LLM through the MCP server or CLI commands.
/// </summary>
public sealed class SetupCommand : Command
{
    public SetupCommand() : base("setup",
        "Print the IdP setup guide. AI agents can use this to orchestrate a full IdP setup with no additional human input beyond the initial login.")
    {
        var jsonOption = new Option<bool>("--json") { Description = "Output as a JSON checklist instead of human-readable text" };
        Options.Add(jsonOption);

        this.SetSafeAction(parseResult =>
        {
            if (parseResult.GetValue(jsonOption))
            {
                PrintJsonGuide();
            }
            else
            {
                PrintGuide();
            }
            return Task.CompletedTask;
        });
    }

    internal static string GetGuideText() => """
        ============================================================
        MRWHOOIDC SETUP GUIDE — AI-NATIVE WALKTHROUGH
        ============================================================

        This guide is written for both humans and LLM agents.
        After the one-time login step, every action below can be
        performed by an AI agent via the MrWhoOidc MCP server or
        directly through mrwho-cli commands.

        ─────────────────────────────────────────────────────────────
        PREREQUISITES
        ─────────────────────────────────────────────────────────────
        1. mrwho-cli installed:
             dotnet tool install --global MrWhoOidc.Cli

        2. MrWhoOidc server running. Docker quickstart:
             git clone https://github.com/popicka70/MrWho.git && cd MrWho
             cp .env.example .env  # edit POSTGRES_PASSWORD, BOOTSTRAP_TOKEN
             docker compose up -d
             # Bootstrap the first tenant (one-time):
             curl -k -X POST https://localhost:8443/bootstrap \
               -H "Content-Type: application/json" \
               -H "X-Bootstrap-Token: $BOOTSTRAP_TOKEN" \
               -d '{"tenantSlug":"default","tenantName":"Default",
                    "adminEmail":"admin@example.com","adminPassword":"ChangeMe123!",
                    "adminName":"Administrator"}'

        ─────────────────────────────────────────────────────────────
        STEP 1 — AUTHENTICATE  [MANUAL — requires browser]
        ─────────────────────────────────────────────────────────────
        Run in a terminal (the user must approve the device code in a browser):

          mrwho-cli login --server https://YOUR_SERVER/t/default

        Options:
          --profile my-prod   name the saved profile
          --server             full URL including /t/<tenant-slug>

        After login, verify with:
          mrwho-cli whoami
          mrwho-cli profile show

        MCP equivalent: call check_auth_status — if "authenticated" is false,
        tell the user to run the login command above.

        ─────────────────────────────────────────────────────────────
        STEP 2 — VERIFY SERVER HEALTH
        ─────────────────────────────────────────────────────────────
        CLI:  mrwho-cli health
        MCP:  server_health

        Expected: all subsystems report "healthy".
        If any are "unhealthy", check Docker logs and try again.

        ─────────────────────────────────────────────────────────────
        STEP 3 — INSPECT EXISTING CONFIGURATION
        ─────────────────────────────────────────────────────────────
        List what already exists before creating anything:

        CLI:  mrwho-cli realm list
              mrwho-cli scope list
              mrwho-cli client list
              mrwho-cli user list

        MCP:  realm_list, scope_list, client_list, user_list

        Note the realm ID — it is required when creating clients.
        The default realm is typically named "default".

        ─────────────────────────────────────────────────────────────
        STEP 4 — CREATE SCOPES (optional, for custom APIs)
        ─────────────────────────────────────────────────────────────
        Create an API scope that clients can request:

        CLI:  mrwho-cli scope create --name api.read --description "Read access to the API"
        MCP:  scope_create { name, description, isExposed }

        Standard OIDC scopes (openid, profile, email) are always available.

        ─────────────────────────────────────────────────────────────
        STEP 5 — CREATE A CLIENT
        ─────────────────────────────────────────────────────────────
        Choose the grant type that matches your application type:

        Web app (authorization code + PKCE):
          mrwho-cli client create \
            --client-id myapp \
            --client-name "My Application" \
            --realm-id <REALM_GUID> \
            --grant-types authorization_code \
            --redirect-uris https://myapp.example.com/callback \
            --scope "openid profile email" \
            --require-pkce true \
            --create-initial-secret

        Machine-to-machine (client credentials):
          mrwho-cli client create \
            --client-id myservice \
            --client-name "My Service" \
            --realm-id <REALM_GUID> \
            --grant-types client_credentials \
            --scope "api.read" \
            --create-initial-secret

        MCP:  client_create { clientId, clientName, realmId, grantTypes,
                               redirectUris, scope, createSecret }
        Returns: client internal ID and (if createSecret) the client secret.

        ─────────────────────────────────────────────────────────────
        STEP 6 — VALIDATE CLIENT CONFIGURATION
        ─────────────────────────────────────────────────────────────
        After creation, always validate:

        CLI:  mrwho-cli client validate <CLIENT_INTERNAL_ID>
        MCP:  client_validate { clientId }

        This checks for missing redirect URIs, missing scopes, missing
        secrets, and other common misconfigurations.

        ─────────────────────────────────────────────────────────────
        STEP 7 — CREATE USERS (if applicable)
        ─────────────────────────────────────────────────────────────
        For web apps with user authentication:

        CLI:  mrwho-cli user create --username alice --email alice@example.com
        MCP:  user_create { username, email, name, password }

        Credentials are written to a secure file (CLI) or returned inline (MCP).

        ─────────────────────────────────────────────────────────────
        STEP 8 — TEST THE OIDC FLOW
        ─────────────────────────────────────────────────────────────
        Inspect discovery to confirm all endpoints are correct:

        CLI:  mrwho-cli discovery --server https://YOUR_SERVER/t/default
        MCP:  server_discovery

        For client_credentials, test the token endpoint directly:
          curl -k -X POST https://YOUR_SERVER/t/default/token \
            -d "grant_type=client_credentials" \
            -d "client_id=myservice" \
            -d "client_secret=<SECRET>" \
            -d "scope=api.read"

        ─────────────────────────────────────────────────────────────
        MCP QUICK-REFERENCE (for AI agents)
        ─────────────────────────────────────────────────────────────
        Run mrwho-cli mcp to start the MCP stdio server, then use these tools:

        STATUS
          setup_guide           Return this guide as text
          check_auth_status     Check if a profile is logged in
          server_health         Check all server health subsystems
          server_discovery      Fetch the OIDC discovery document

        REALMS
          realm_list            List realms in the current tenant

        CLIENTS
          client_list           List clients
          client_get            Get client details by internal ID
          client_create         Create a new OIDC client
          client_validate       Validate client configuration

        SCOPES
          scope_list            List scopes
          scope_create          Create a new scope

        USERS
          user_list             List users
          user_create           Create a new user

        ─────────────────────────────────────────────────────────────
        NOTES FOR AI AGENTS
        ─────────────────────────────────────────────────────────────
        - Login (Step 1) is the ONLY step that requires human interaction.
          Everything else can be performed autonomously via MCP tools or CLI.
        - Always call check_auth_status before attempting API operations.
          If unauthenticated, instruct the user to run: mrwho-cli login
        - check_auth_status requires NO server connection — it reads local config.
        - Always call realm_list first to obtain the realmId required for client_create.
        - After client_create, always call client_validate to confirm correctness.
        - Tokens have a limited lifetime. If API calls fail with 401, re-check auth.
        - Use --dry-run on CLI commands to preview write operations safely.

        Full documentation: https://mrwhooidc.com/getting-started.html
        NuGet package:      https://www.nuget.org/packages/MrWhoOidc.Cli
        ============================================================
        """;

    private static void PrintGuide()
    {
        AnsiConsole.MarkupLine("[bold cyan]mrwho-cli setup guide[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(GetGuideText());
    }

    private static void PrintJsonGuide()
    {
        var guide = new
        {
            title = "MrWhoOidc Setup Guide",
            version = "1.0",
            description = "Complete IdP setup workflow. Only Step 1 (login) requires human interaction. All other steps can be performed by an AI agent via MCP tools or CLI commands.",
            mcpServer = "mrwho-cli mcp",
            steps = new[]
            {
                new { step = 1, name = "Authenticate", manual = true, description = "Run: mrwho-cli login --server https://YOUR_SERVER/t/<tenant-slug>", mcpTool = "check_auth_status (to verify after login)" },
                new { step = 2, name = "Verify Health", manual = false, description = "Check all server subsystems are healthy", mcpTool = "server_health" },
                new { step = 3, name = "Inspect Configuration", manual = false, description = "List existing realms, scopes, clients, users", mcpTool = "realm_list, scope_list, client_list, user_list" },
                new { step = 4, name = "Create Scopes", manual = false, description = "Create any custom API scopes needed by your clients", mcpTool = "scope_create" },
                new { step = 5, name = "Create Client", manual = false, description = "Register your application as an OIDC client", mcpTool = "client_create" },
                new { step = 6, name = "Validate Client", manual = false, description = "Validate the created client configuration for issues", mcpTool = "client_validate" },
                new { step = 7, name = "Create Users", manual = false, description = "Create user accounts if the application needs user auth", mcpTool = "user_create" },
                new { step = 8, name = "Test Flow", manual = false, description = "Inspect discovery and test token endpoint", mcpTool = "server_discovery" }
            },
            mcpTools = new[]
            {
                "setup_guide", "check_auth_status", "server_health", "server_discovery",
                "realm_list", "client_list", "client_get", "client_create", "client_validate",
                "scope_list", "scope_create", "user_list", "user_create"
            }
        };
        AnsiConsole.WriteLine(System.Text.Json.JsonSerializer.Serialize(guide, SharedJsonOptions.IndentedOptions));
    }
}
