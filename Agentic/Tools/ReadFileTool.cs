using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class ReadFileTool : ITool
{
    public string Name => "read_file";
    public string Description => "Read the contents of a file at the given absolute path.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "The absolute path to the file to read."
            }
        },
        "required": ["path"],
        "additionalProperties": false
    }
    """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString()!;
        if (!File.Exists(path))
            return new ToolResult($"File not found: {path}", IsError: true);

        var content = await File.ReadAllTextAsync(path, ct);
        return new ToolResult(content);
    }
}
