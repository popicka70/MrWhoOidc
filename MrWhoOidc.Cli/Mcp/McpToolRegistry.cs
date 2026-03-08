using System.Text.Json;

namespace MrWhoOidc.Cli.Mcp;

/// <summary>
/// Registry of MCP tools that map to CLI commands.
/// Each tool exposes a CLI operation to LLMs via JSON-RPC.
/// </summary>
public sealed class McpToolRegistry
{
    private readonly Dictionary<string, McpToolDefinition> _tools = new();

    public McpToolRegistry()
    {
        RegisterDefaultTools();
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
        {
            throw new KeyNotFoundException($"Tool '{toolName}' not found");
        }

        return await tool.Handler(arguments ?? new(), ct);
    }

    private void RegisterDefaultTools()
    {
        // Phase 4+ will add actual tool implementations
        // For now, register placeholder tools to demonstrate MCP structure
        
        RegisterTool(new McpToolDefinition
        {
            Name = "health_check",
            Description = "Check if the CLI is configured and can reach the OIDC server",
            InputSchema = CreateSchema(new { }),
            Handler = async (args, ct) =>
            {
                await Task.CompletedTask;
                return new object[] 
                { 
                    new { type = "text", text = "MCP server is operational. Authentication and admin tools will be available after login." }
                };
            }
        });
    }

    private void RegisterTool(McpToolDefinition tool)
    {
        _tools[tool.Name] = tool;
    }

    private static JsonElement CreateSchema(object schemaObj)
    {
        return JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = schemaObj,
            required = Array.Empty<string>()
        });
    }
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
