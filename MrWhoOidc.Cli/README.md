# MrWhoOidc.Cli

Command-line interface for managing MrWhoOidc OIDC server with built-in MCP (Model Context Protocol) support for LLM integration.

## Features

- **Dual-Mode Operation**:
  - **CLI Mode**: Traditional command-line interface with human-friendly output (tables, colors, progress)
  - **MCP Mode**: JSON-RPC 2.0 stdio server for direct LLM tool integration (VS Code Copilot, Claude Desktop, etc.)

- **Authentication**: Device Code Flow (RFC 8628) with automatic token refresh
- **Multi-Tenancy Aware**: Respects tenant boundaries; platform-admin can operate cross-tenant
- **Comprehensive Admin Operations**: Manage clients, users, roles, realms, scopes, identity providers, tenants, keys, and more

## Installation

### As a .NET Global Tool (Production)

```bash
dotnet tool install --global MrWhoOidc.Cli
```

### From Source (Development)

```bash
# Build, bump the patch version, pack, and reinstall the global tool from the local package source
./deploy-mrwho-cli.sh

# Bump the minor version instead of the default patch increment
./deploy-mrwho-cli.sh --bump-part minor

# Pack without bumping the version
./deploy-mrwho-cli.sh --no-bump-version
```

The deploy script packs `MrWhoOidc.Cli`, removes any existing global install, and reinstalls the tool from `./nupkg`. The repository now includes a local NuGet source declaration in `NuGet.config` pointing at `./nupkg`, and the deploy scripts auto-bump the package version by default. Use `--skip-install` if you only want to produce the `.nupkg` artifact. A PowerShell variant is also available as `deploy-mrwho-cli.ps1` if you prefer `pwsh`.

### Local NuGet Feed

The repository-local NuGet feed is defined in `NuGet.config`:

```xml
<add key="MrWhoOidcLocal" value="./nupkg" />
```

After running the deploy script, you can install the tool directly from the local package feed:

```bash
dotnet tool install --global --add-source ./nupkg MrWhoOidc.Cli
```

## Quick Start

### CLI Mode

```bash
# Login to your tenant-aware OIDC server
mrwho-cli login --server https://auth.example.com/t/acme

# Follow the device code flow instructions in your browser

# Inspect the connected server discovery document
mrwho-cli discovery --server https://auth.example.com/t/acme

# Export a tenant manifest as a platform admin
mrwho-cli export tenant acme --output ./exports/

# Export a client manifest from the current tenant profile
mrwho-cli export client 2d6f0d17-5400-4c5b-a65a-fbb7b360f404 --output ./exports/

# List clients for the current tenant
mrwho-cli client list

# List scopes for the current tenant
mrwho-cli scope list

# List all tenants as a platform admin
mrwho-cli tenant list

# List saved profiles
mrwho-cli profile list

# Show the active profile
mrwho-cli profile show

# Clear tokens for the current profile
mrwho-cli logout

# Get help for any command
mrwho-cli discovery --help
```

### MCP Mode (for LLMs)

```bash
# Start MCP server (stdio transport)
mrwho-cli mcp
```

Configure in your MCP client (e.g., VS Code settings.json):

```json
{
  "mcpServers": {
    "mrwho": {
      "command": "mrwho-cli",
      "args": ["mcp"]
    }
  }
}
```

## Project Status

**Phase 1: Core Infrastructure** ✅ **COMPLETE**
- Project structure with PackAsTool configuration
- Configuration model with profile support (`~/.mrwhooidc/config.json`)
- MCP JSON-RPC 2.0 protocol implementation
- Basic command framework with System.CommandLine
- Spectre.Console integration for rich terminal UI

**Phase 2: Authentication and Discovery** 🚧 **IN PROGRESS**
- Device code flow login
- Server discovery inspection
- Working profile management (`list`, `show`, `switch`, `remove`)
- Working logout/token clearing
- Authenticated admin list commands for tenants, clients, and scopes
- File-first export commands for tenant, realm, client, and provider manifests

**Phase 3+**: CRUD admin commands, provider/admin parity, MCP parity, richer output formatting, and broader server configuration support.

## Architecture

```
MrWhoOidc.Cli/
├── Program.cs                  # Entry point with mode detection
├── Configuration/
│   └── CliConfig.cs           # Config model & persistence
├── Mcp/
│   ├── McpServer.cs           # JSON-RPC stdio server
│   ├── McpModels.cs           # MCP protocol DTOs
│   └── McpToolRegistry.cs     # Tool definitions & handlers
├── Commands/
│   ├── LoginCommand.cs        # Device code flow auth
│   ├── LogoutCommand.cs       # Clear tokens for a profile
│   ├── ProfileCommand.cs      # Profile list/show/switch/remove
│   ├── DiscoveryCommand.cs    # Inspect OIDC discovery metadata
│   ├── ExportCommand.cs       # Export manifests to files
│   ├── TenantCommand.cs       # Platform tenant listing
│   ├── ClientCommand.cs       # Tenant/platform client listing
│   └── ScopeCommand.cs        # Tenant/platform scope listing
├── Services/
│   ├── CliServerConnection.cs # Shared server/discovery/auth helpers
│   └── CliAdminApiClient.cs   # Authenticated admin API requests
│   └── CliFileOutput.cs       # Secure file-first output helpers
└── (upcoming: Http/, Output/, admin command groups)
```

## Configuration

Configuration stored at `~/.mrwhooidc/config.json`:

```json
{
  "currentProfile": "default",
  "profiles": {
    "default": {
      "serverUrl": "https://auth.example.com",
      "clientId": "mrwho-cli-acme",
      "accessToken": "...",
      "refreshToken": "...",
      "tokenExpiry": "2026-03-08T12:00:00Z",
      "tenantSlug": "acme",
      "isPlatformAdmin": false
    }
  }
}
```

## Development

### Build

```bash
dotnet build MrWhoOidc.Cli/MrWhoOidc.Cli.csproj
```

### Run from source

```bash
dotnet run --project MrWhoOidc.Cli/MrWhoOidc.Cli.csproj -- [command] [options]
```

### Test MCP mode

```bash
# Start MCP server
dotnet run --project MrWhoOidc.Cli/MrWhoOidc.Cli.csproj -- mcp

# In another terminal, send JSON-RPC request
echo '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","clientInfo":{"name":"test","version":"1.0"}}}' | dotnet run --project MrWhoOidc.Cli/MrWhoOidc.Cli.csproj -- mcp
```

## Dependencies

- **System.CommandLine** (2.0.0-beta4): Modern command-line parsing
- **Spectre.Console** (0.49.1): Rich terminal UI (tables, spinners, prompts)
- **Microsoft.Extensions.Http.Polly**: Resilient HTTP with retries
- **System.IdentityModel.Tokens.Jwt**: JWT parsing for token introspection
- **MrWhoOidc.Auth**: Shared models and protocol constants

## Roadmap

See [/memories/session/plan.md](/memories/session/plan.md) for the current phased implementation plan covering:

- shared CLI infrastructure and token/session improvements
- immediate command groups against existing endpoints
- follow-on WebAuth API work for clients/realms/scopes/users
- CLI and MCP testing strategy
- documentation and rollout work

## Contributing

Follow the existing patterns:
- Commands in `Commands/` directory, one class per command group
- Use Spectre.Console for all terminal output
- MCP tools registered in `McpToolRegistry` with JSON Schema
- Async/await throughout, proper cancellation token handling
- Match .NET 10 patterns from main codebase

## License

MIT - See root LICENSE file
