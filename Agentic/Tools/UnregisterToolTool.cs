using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class UnregisterToolTool : ITool
{
    private readonly ToolRegistry _registry;
    private readonly Func<string, Task<bool>> _approveDelete;

    public UnregisterToolTool(ToolRegistry registry, Func<string, Task<bool>> approveDelete)
    {
        _registry = registry;
        _approveDelete = approveDelete;
    }

    public string Name => "unregister_tool";
    public string Description =>
        "Unregister a previously-authored dynamic tool. The user will be asked whether to also delete its on-disk folder.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "name": { "type": "string", "description": "The exact name of the tool to unregister." }
        },
        "required": ["name"],
        "additionalProperties": false
    }
    """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var name = arguments.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(name))
            return new ToolResult("name must be a non-empty string", IsError: true);

        var pt = _registry.Unregister(name);
        if (pt is null)
            return new ToolResult(
                $"No dynamic tool named '{name}' is registered. (Built-in tools cannot be unregistered.)",
                IsError: true);

        if (await _approveDelete(name))
        {
            try
            {
                Directory.Delete(pt.FolderPath, recursive: true);
                return new ToolResult($"Unregistered '{name}' and deleted its folder at {pt.FolderPath}.");
            }
            catch (Exception ex)
            {
                return new ToolResult(
                    $"Unregistered '{name}' but could not delete folder: {ex.Message}. " +
                    "It may still be locked; try deleting manually.",
                    IsError: true);
            }
        }

        return new ToolResult($"Unregistered '{name}'. Folder kept at {pt.FolderPath}.");
    }
}
