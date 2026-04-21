# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Agentic is a .NET 10 console application that connects to a locally hosted LLM (via LM Studio's OpenAI-compatible API) to provide an interactive AI assistant with tool-calling capabilities. It is an offline, local-system alternative to cloud-based coding assistants.

## Build & Run

```bash
dotnet build Agentic
dotnet run --project Agentic
```

Requires .NET 10 SDK (preview). No test projects exist yet.

## Architecture

Single-project solution (`Agentic.slnx` → `Agentic/Agentic.csproj`). Two key namespaces:

**`Agentic.Core`** — Framework infrastructure:
- `ITool` / `ToolResult` — Tool contract. Tools declare a `Name`, `Description`, JSON Schema via `BinaryData ParameterSchema`, and `ExecuteAsync(JsonElement)`.
- `ToolRegistry` — Stores tools by name, converts them to `ChatTool` format for the OpenAI SDK.
- `Agent` — The agentic loop: appends user message to `_history` → sends to LLM with tool definitions → if `FinishReason.ToolCalls`, executes each tool and appends results → loops until `FinishReason.Stop` or max iterations (default 20).

**`Agentic.Tools`** — Built-in tool implementations (`read_file`, `write_file`, `list_directory`, `run_command`). `RunCommandTool` invokes `pwsh` (PowerShell Core).

**`Program.cs`** — Top-level statements: configures the LM Studio endpoint/model, registers tools, creates the Agent, runs a REPL loop (`exit`/`reset` commands).

## Key Conventions

- The `OpenAI` NuGet package (v2.9.1) is used with LM Studio's OpenAI-compatible endpoint, not the actual OpenAI API. The API key `"lm-studio"` is a placeholder.
- `OPENAI001` warning is suppressed in the csproj (experimental SDK features).
- Adding a new tool: implement `ITool` in `Tools/`, then register it in `Program.cs` via `tools.Register(new MyTool())`.
- Tool parameter schemas are inline JSON Schema strings wrapped in `BinaryData.FromString()`.
- The `Agent` supports configurable `ChatReasoningEffortLevel` for models that support reasoning.
