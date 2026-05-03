using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class RequestNewToolTool : ITool
{
    private readonly ToolFactory _factory;
    private readonly ToolRegistry _registry;
    private readonly Func<ToolProposal, Task<bool>> _approve;

    public RequestNewToolTool(
        ToolFactory factory,
        ToolRegistry registry,
        Func<ToolProposal, Task<bool>> approve)
    {
        _factory = factory;
        _registry = registry;
        _approve = approve;
    }

    public string Name => "request_new_tool";
    public string Description =>
        "Request a brand new tool be authored by a code-generation LLM when no existing tool fits. " +
        "The user will be asked to approve the generated tool before it is registered. " +
        "After approval, the new tool will be available on your next turn.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "intent": {
                "type": "string",
                "description": "A clear description of what the tool should do, including its inputs and outputs. Example: 'Convert all .heic files in a directory to .jpg using ImageMagick. Input: directory path. Output: list of converted files.'"
            }
        },
        "required": ["intent"],
        "additionalProperties": false
    }
    """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var intent = arguments.GetProperty("intent").GetString();
        if (string.IsNullOrWhiteSpace(intent))
            return new ToolResult("intent must be a non-empty string", IsError: true);

        ToolProposal proposal;
        try
        {
            proposal = await _factory.ProposeAsync(intent, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Tool authoring failed: {ex.Message}", IsError: true);
        }

        if (_registry.Get(proposal.Tool.Name) is not null)
        {
            proposal.LoadContext.Unload();
            return new ToolResult(
                $"A tool named '{proposal.Tool.Name}' already exists. Pick a different intent or unregister the existing tool first.",
                IsError: true);
        }

        bool approved;
        try { approved = await _approve(proposal); }
        catch (Exception ex)
        {
            proposal.LoadContext.Unload();
            return new ToolResult($"Approval prompt failed: {ex.Message}", IsError: true);
        }

        if (!approved)
        {
            proposal.LoadContext.Unload();
            return new ToolResult("User declined the proposed tool. No tool was registered.", IsError: true);
        }

        PersistedTool persisted;
        try
        {
            persisted = await _factory.PersistAsync(proposal, ct);
        }
        catch (Exception ex)
        {
            proposal.LoadContext.Unload();
            return new ToolResult($"Persistence failed: {ex.Message}", IsError: true);
        }

        _registry.RegisterPersisted(persisted);
        return new ToolResult(
            $"Registered new tool '{persisted.Tool.Name}'. It is now callable on your next turn. " +
            $"Description: {persisted.Tool.Description}");
    }
}
