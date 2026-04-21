# Agentic

A .NET console application that connects to a locally hosted LLM (via LM Studio) to provide an interactive AI assistant with tool-calling capabilities. The goal is to build an offline AI system — similar to Claude Code — that can interact with your local system through agents and tools.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [LM Studio](https://lmstudio.ai/) running locally with a model that supports function/tool calling
- Recommended models: Qwen 2.5/3 Coder, DeepSeek Coder V2/V3, Mistral with function calling

## Getting Started

1. Start LM Studio and load a model with function calling support.
2. Update the endpoint and model name in `Program.cs`:
   ```csharp
   const string lmStudioEndpoint = "http://localhost:1234/v1";
   const string modelName = ""; // empty = use whatever is loaded
   ```
3. Run the application:
   ```
   dotnet run --project Agentic
   ```
4. Type messages at the `>` prompt. Type `exit` to quit or `reset` to clear conversation history.

## Architecture

```
Agentic/
├── Core/                   # Framework infrastructure
│   ├── ITool.cs            # Tool interface and ToolResult record
│   ├── ToolRegistry.cs     # Collects tools, converts to OpenAI ChatTool format
│   └── Agent.cs            # Agentic loop: prompt → tool_calls → execute → loop
├── Tools/                  # Built-in tool implementations
│   ├── ReadFileTool.cs     # Read file contents
│   ├── WriteFileTool.cs    # Write/create files
│   ├── ListDirectoryTool.cs# List directory contents (shallow or recursive)
│   └── RunCommandTool.cs   # Execute PowerShell commands
└── Program.cs              # REPL entry point and configuration
```

## How It Works

### The Agentic Loop

The core of the system is the **agentic loop** in `Agent.RunAsync()`. This is the same pattern used by tools like Claude Code:

```
User message
     │
     ▼
┌──────────────┐
│  Send to LLM │◄─────────────────────┐
└──────┬───────┘                       │
       │                               │
       ▼                               │
  FinishReason?                        │
       │                               │
       ├── ToolCalls ──► Execute ──► Append results
       │                               │
       └── Stop ──► Return text response
```

1. The user's message is appended to conversation history.
2. The full history (including tool definitions) is sent to the LLM.
3. If the LLM responds with `FinishReason.ToolCalls`, each tool is executed and results are appended to history. The loop repeats.
4. If the LLM responds with a text message (`FinishReason.Stop`), it's returned to the user.
5. A safety cap (default 20 iterations) prevents infinite loops.

### Tool System

Tools implement the `ITool` interface:

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    BinaryData ParameterSchema { get; }  // JSON Schema
    Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default);
}
```

- **Name** and **Description** tell the LLM what the tool does.
- **ParameterSchema** is a JSON Schema defining the tool's parameters — the LLM uses this to generate correct arguments.
- **ExecuteAsync** receives the parsed arguments and returns a `ToolResult`.

Tools are registered in a `ToolRegistry`, which converts them to the OpenAI `ChatTool` format for the API request.

### Adding a New Tool

1. Create a class in `Tools/` implementing `ITool`.
2. Define the parameter schema as JSON Schema.
3. Implement `ExecuteAsync` with your logic.
4. Register it in `Program.cs`:
   ```csharp
   tools.Register(new MyNewTool());
   ```

The LLM will automatically see and be able to use the new tool.

## Built-in Tools

| Tool | Description |
|---|---|
| `read_file` | Reads the contents of a file at an absolute path. |
| `write_file` | Writes content to a file. Creates parent directories if needed. |
| `list_directory` | Lists directory contents with file sizes and subdirectory item counts. Supports recursive listing. When subdirectories contain items, the LLM is instructed to ask if you want to explore deeper. |
| `run_command` | Executes a PowerShell command and returns stdout/stderr. |

## Reasoning Effort

For models that support reasoning (e.g., GPT-OSS, Qwen3), you can control the reasoning effort per agent:

```csharp
var agent = new Agent(
    name: "Assistant",
    systemPrompt: "...",
    chatClient: chatClient,
    tools: tools,
    reasoningEffort: ChatReasoningEffortLevel.Medium);

// Or change at runtime
agent.ReasoningEffort = ChatReasoningEffortLevel.High;
```

| Level | Use case |
|---|---|
| `Low` | Simple tool routing, file reads, directory listings |
| `Medium` | General conversation, multi-step tasks (good default) |
| `High` | Complex code analysis, planning, debugging |

Models that don't support this parameter will ignore it gracefully.

## LLM Compatibility

The application uses the OpenAI-compatible API that LM Studio exposes. Not all models handle tool calling equally well. Look for models that explicitly list **Function Calling** in their LM Studio capabilities.

The API key is ignored by LM Studio but required by the SDK — `"lm-studio"` is used as a placeholder.

# ⚠️ Caution ⚠️
This project is present to you as-is, meaning I am in no way responsible if you try it out for yourself and experience damages or breaches. This is purely for entertainment and informational purposes.
