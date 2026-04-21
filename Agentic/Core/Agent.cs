using System.Text.Json;
using OpenAI.Chat;

namespace Agentic.Core;

public class Agent
{
    public string Name { get; }
    public string SystemPrompt { get; }
    public ChatReasoningEffortLevel? ReasoningEffort { get; set; }

    private readonly ChatClient _chatClient;
    private readonly ToolRegistry _tools;
    private readonly List<ChatMessage> _history = new();
    private readonly int _maxIterations;

    public Agent(
        string name,
        string systemPrompt,
        ChatClient chatClient,
        ToolRegistry tools,
        int maxIterations = 20,
        ChatReasoningEffortLevel? reasoningEffort = null)
    {
        Name = name;
        SystemPrompt = systemPrompt;
        _chatClient = chatClient;
        _tools = tools;
        _maxIterations = maxIterations;
        ReasoningEffort = reasoningEffort;

        _history.Add(ChatMessage.CreateSystemMessage(systemPrompt));
    }

    /// <summary>
    /// Send a user message and run the agentic tool-call loop until the LLM
    /// produces a final text response (or we hit the iteration cap).
    /// </summary>
    public async Task<string> RunAsync(string userMessage, CancellationToken ct = default)
    {
        _history.Add(ChatMessage.CreateUserMessage(userMessage));

        var options = new ChatCompletionOptions();
        foreach (var tool in _tools.ToChatTools())
            options.Tools.Add(tool);

        if (ReasoningEffort is not null)
            options.ReasoningEffortLevel = ReasoningEffort;

        for (int i = 0; i < _maxIterations; i++)
        {
            ChatCompletion completion = await _chatClient.CompleteChatAsync(_history, options, ct);

            if (completion.FinishReason == ChatFinishReason.ToolCalls)
            {
                // Add the assistant message containing the tool calls
                _history.Add(ChatMessage.CreateAssistantMessage(completion));

                // Execute each tool call
                foreach (var toolCall in completion.ToolCalls)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  [{Name}] calling tool: {toolCall.FunctionName}");
                    Console.ResetColor();

                    var tool = _tools.Get(toolCall.FunctionName);
                    ToolResult result;

                    if (tool is null)
                    {
                        result = new ToolResult($"Unknown tool: {toolCall.FunctionName}", IsError: true);
                    }
                    else
                    {
                        try
                        {
                            var args = JsonDocument.Parse(toolCall.FunctionArguments).RootElement;
                            result = await tool.ExecuteAsync(args, ct);
                        }
                        catch (Exception ex)
                        {
                            result = new ToolResult($"Tool execution failed: {ex.Message}", IsError: true);
                        }
                    }

                    if (result.IsError)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [{Name}] tool error: {result.Output}");
                        Console.ResetColor();
                    }

                    _history.Add(ChatMessage.CreateToolMessage(toolCall.Id, result.Output));
                }

                continue; // Loop back so the LLM sees the tool results
            }

            // Final text response
            var text = string.Join("", completion.Content
                .Where(c => c.Kind == ChatMessageContentPartKind.Text)
                .Select(c => c.Text));

            _history.Add(ChatMessage.CreateAssistantMessage(text));
            return text;
        }

        return $"[{Name}] Reached maximum iterations ({_maxIterations}) without a final response.";
    }

    /// <summary>
    /// Clear conversation history (keeps the system prompt).
    /// </summary>
    public void Reset()
    {
        _history.Clear();
        _history.Add(ChatMessage.CreateSystemMessage(SystemPrompt));
    }
}
