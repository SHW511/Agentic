using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class ListDynamicToolsTool : ITool
{
    private readonly ToolRegistry _registry;

    public ListDynamicToolsTool(ToolRegistry registry) { _registry = registry; }

    public string Name => "list_dynamic_tools";
    public string Description => "List all dynamically-authored tools currently registered, with their metadata.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    { "type": "object", "properties": {}, "additionalProperties": false }
    """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var persisted = _registry.AllPersisted;
        if (persisted.Count == 0)
            return Task.FromResult(new ToolResult("No dynamic tools registered."));

        var lines = persisted.Select(p =>
            $"- {p.Meta.Name}: {p.Meta.Description} (created {p.Meta.CreatedAt:yyyy-MM-dd}, by {p.Meta.CoderModel})");
        return Task.FromResult(new ToolResult(string.Join("\n", lines)));
    }
}
