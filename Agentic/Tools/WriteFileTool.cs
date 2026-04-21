using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class WriteFileTool : ITool
{
    public string Name => "write_file";
    public string Description => "Write content to a file at the given absolute path. Creates the file if it doesn't exist, overwrites if it does.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "The absolute path to the file to write."
            },
            "content": {
                "type": "string",
                "description": "The content to write to the file."
            }
        },
        "required": ["path", "content"],
        "additionalProperties": false
    }
    """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString()!;
        var content = arguments.GetProperty("content").GetString()!;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(path, content, ct);
        return new ToolResult($"File written: {path}");
    }
}
