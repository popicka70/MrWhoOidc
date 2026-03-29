using System.Text.Json;
using System.Text.Json.Serialization;

namespace MrWhoOidc.Cli;

/// <summary>
/// Shared JsonSerializerOptions instances to avoid repeated allocations.
/// </summary>
internal static class SharedJsonOptions
{
    public static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public static readonly JsonSerializerOptions IndentedSkipNullOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
