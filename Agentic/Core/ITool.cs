using System.Text.Json;

namespace Agentic.Core;

/// <summary>
/// A tool that the LLM can invoke during a conversation.
/// </summary>
public interface ITool
{
    string Name { get; }
    string Description { get; }
    BinaryData ParameterSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);
}

public record ToolResult(string Output, bool IsError = false);
