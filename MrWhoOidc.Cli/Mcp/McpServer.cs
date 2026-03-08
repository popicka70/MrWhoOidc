using System.Text.Json;
using System.Text.Json.Nodes;

namespace MrWhoOidc.Cli.Mcp;

/// <summary>
/// MCP (Model Context Protocol) JSON-RPC 2.0 server for LLM integration.
/// Implements stdio transport for standard MCP clients (VS Code, Claude Desktop, etc.).
/// </summary>
public sealed class McpServer
{
    private readonly McpToolRegistry _toolRegistry = new();
    private bool _initialized;
    private string _clientInfo = "unknown";

    public async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        using var reader = new StreamReader(input);
        using var writer = new StreamWriter(output) { AutoFlush = true };

        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            
            if (line == null)
            {
                break; // EOF
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var request = JsonSerializer.Deserialize<JsonRpcRequest>(line);
                if (request == null)
                {
                    await WriteErrorAsync(writer, null, -32700, "Parse error");
                    continue;
                }

                var response = await HandleRequestAsync(request, ct);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response));
            }
            catch (JsonException)
            {
                await WriteErrorAsync(writer, null, -32700, "Parse error");
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(writer, null, -32603, $"Internal error: {ex.Message}");
            }
        }
    }

    private async Task<JsonRpcResponse> HandleRequestAsync(JsonRpcRequest request, CancellationToken ct)
    {
        return request.Method switch
        {
            "initialize" => await HandleInitializeAsync(request, ct),
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolsCallAsync(request, ct),
            "resources/list" => HandleResourcesList(request),
            _ => CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
        };
    }

    private async Task<JsonRpcResponse> HandleInitializeAsync(JsonRpcRequest request, CancellationToken ct)
    {
        var parameters = request.Params?.Deserialize<InitializeParams>();
        _clientInfo = parameters?.ClientInfo?.Name ?? "unknown";
        _initialized = true;

        var result = new InitializeResult
        {
            ProtocolVersion = "2024-11-05",
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { },
                Resources = new ResourcesCapability { }
            },
            ServerInfo = new ServerInfo
            {
                Name = "mrwho-cli",
                Version = "1.0.0"
            }
        };

        await Task.CompletedTask; // Async for consistency
        return CreateSuccessResponse(request.Id, result);
    }

    private JsonRpcResponse HandleToolsList(JsonRpcRequest request)
    {
        if (!_initialized)
        {
            return CreateErrorResponse(request.Id, -32002, "Server not initialized");
        }

        var tools = _toolRegistry.GetAllTools();
        var result = new ToolsListResult { Tools = tools };
        
        return CreateSuccessResponse(request.Id, result);
    }

    private async Task<JsonRpcResponse> HandleToolsCallAsync(JsonRpcRequest request, CancellationToken ct)
    {
        if (!_initialized)
        {
            return CreateErrorResponse(request.Id, -32002, "Server not initialized");
        }

        var parameters = request.Params?.Deserialize<ToolCallParams>();
        if (parameters == null || string.IsNullOrEmpty(parameters.Name))
        {
            return CreateErrorResponse(request.Id, -32602, "Invalid params: name required");
        }

        try
        {
            var result = await _toolRegistry.ExecuteToolAsync(parameters.Name, parameters.Arguments, ct);
            return CreateSuccessResponse(request.Id, new ToolCallResult { Content = result });
        }
        catch (KeyNotFoundException)
        {
            return CreateErrorResponse(request.Id, -32602, $"Unknown tool: {parameters.Name}");
        }
        catch (Exception ex)
        {
            return CreateErrorResponse(request.Id, -32603, $"Tool execution failed: {ex.Message}");
        }
    }

    private JsonRpcResponse HandleResourcesList(JsonRpcRequest request)
    {
        if (!_initialized)
        {
            return CreateErrorResponse(request.Id, -32002, "Server not initialized");
        }

        // Resources support is optional - return empty list for now
        var result = new ResourcesListResult { Resources = Array.Empty<Resource>() };
        return CreateSuccessResponse(request.Id, result);
    }

    private static JsonRpcResponse CreateSuccessResponse(object? id, object result)
    {
        return new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = id,
            Result = JsonSerializer.SerializeToElement(result)
        };
    }

    private static JsonRpcResponse CreateErrorResponse(object? id, int code, string message)
    {
        return new JsonRpcResponse
        {
            Jsonrpc = "2.0",
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        };
    }

    private static async Task WriteErrorAsync(StreamWriter writer, object? id, int code, string message)
    {
        var response = CreateErrorResponse(id, code, message);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response));
    }
}
