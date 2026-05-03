# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Agentic is a .NET 10 console application that connects to a locally hosted LLM (via LM Studio's OpenAI-compatible API) to provide an interactive AI assistant with tool-calling capabilities. It is an offline, local-system alternative to cloud-based coding assistants.

It also supports **runtime self-extension**: the agent can request brand-new tools to be authored on the fly by a separate code-generation LLM, compiled to a DLL via Roslyn, persisted to disk, and made callable on its very next turn.

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
- `ToolRegistry` — Stores tools by name; tracks `PersistedTool` records separately so dynamic tools can be unloaded via their `AssemblyLoadContext`. Converts tools to `ChatTool` format for the OpenAI SDK.
- `Agent` — The agentic loop: appends user message to `_history` → sends to LLM with tool definitions → if `FinishReason.ToolCalls`, executes each tool and appends results → loops until `FinishReason.Stop` or max iterations (default 20). Rebuilds the tool list on every iteration so newly-registered dynamic tools become callable mid-conversation.
- `ToolFactory` — Coder-LLM-driven pipeline: propose → static-analyze → Roslyn compile → ALC load → optional persist. Used by the `request_new_tool` meta-tool.
- `DynamicToolLoadContext` — Collectible `AssemblyLoadContext` per dynamic tool, so a tool can be unregistered and its DLL freed.
- `DynamicToolTypes.cs` — `ToolMeta`, `ToolProposal`, `PersistedTool` records.

**`Agentic.Tools`** — Built-in tool implementations: `read_file`, `write_file`, `list_directory`, `run_command`, `web_search`, `web_fetch`, plus three meta-tools (`request_new_tool`, `list_dynamic_tools`, `unregister_tool`). `RunCommandTool` invokes `cmd` on Windows.

**`Program.cs`** — Top-level statements: configures two LM Studio `ChatClient`s (chat model + coder model), registers tools, creates the `ToolFactory`, loads any previously-persisted dynamic tools from `~/.agentic/tools/`, creates the `Agent`, runs a REPL loop (`exit`/`reset` commands). Hosts the synchronous REPL approval prompts (`y/N`) used by `request_new_tool` and `unregister_tool`.

## Dynamic Tool System

The agent can author its own tools at runtime through a two-LLM design:

- **Chat model** (default: `nvidia/nemotron-3-nano-omni`) — drives the main agent loop.
- **Coder model** (default: `qwen2.5-coder-7b-instruct`) — invoked only by `ToolFactory` when the chat model calls `request_new_tool`. Both must be loaded simultaneously in LM Studio.

**Pipeline (per `request_new_tool` invocation):**
1. Chat model emits an `intent` description.
2. `ToolFactory.ProposeAsync` calls the coder model with a system prompt requiring two fenced output blocks: a ` ```json ` metadata block + a ` ```csharp ` source block. The fenced-block envelope avoids the JSON-in-JSON escaping that small models cannot reliably handle.
3. `NormalizeSource` strips any usings the model wrote and auto-injects a fixed list of allowed usings. The source must NOT be wrapped in a `namespace { }` (the type lookup expects global namespace).
4. Static analysis: forbidden API substring scan + using-directive allowlist check.
5. Roslyn compiles to in-memory bytes via `CSharpCompilation.Emit`.
6. Loaded into a fresh `DynamicToolLoadContext` via `LoadFromStream` (not `LoadFromAssemblyPath` — the latter locks the file on Windows).
7. On compile/parse failure, the diagnostic is fed back to the coder model for up to 3 retries.
8. User is prompted at the REPL with name + description + source + `y/N`.
9. On accept, `PersistAsync` writes `tool.dll`, `tool.pdb`, `source.cs`, and `meta.json` into `~/.agentic/tools/<tool_name>/`.

**Persistence layout:**
```
~/.agentic/tools/<tool_name>/
  tool.dll      # Roslyn-emitted assembly
  tool.pdb      # portable PDB for debugging
  source.cs     # original C# (source of truth — used for recompile on load failure)
  meta.json     # { name, description, parameter_schema, coder_model,
                #   source_intent, core_assembly_version, source_sha256, created_at }
```

On startup, every subdirectory is loaded via `ToolFactory.LoadFromDisk`. If the DLL fails to load (e.g. `Agentic.Core` version drift), it auto-recompiles from `source.cs`.

**Reference set & allowlist** (in `ToolFactory.BuildReferenceSet` and `ToolFactory.AllowedNamespacePrefixes`): `System`, `System.IO`, `System.Text(.Json|.RegularExpressions)`, `System.Diagnostics`, `System.Net.Http(.Json)`, `System.Threading(.Tasks)`, `System.Collections(.Generic)`, `System.Linq`, `System.Security.Cryptography`, `Agentic.Core`. To allow new namespaces, add to BOTH the allowlist (for using-directive check) AND the wanted-assemblies list in `BuildReferenceSet` (so it actually links). `BinaryData` requires `System.Memory.Data` — the factory references it explicitly via `typeof(BinaryData).Assembly.Location` as a belt-and-braces measure.

## Key Conventions

- The `OpenAI` NuGet package (v2.9.1) is used with LM Studio's OpenAI-compatible endpoint, not the actual OpenAI API. The API key `"lm-studio"` is a placeholder.
- `OPENAI001` warning is suppressed in the csproj (experimental SDK features).
- Adding a new built-in tool: implement `ITool` in `Tools/`, then register it in `Program.cs` via `tools.Register(new MyTool())`.
- Tool parameter schemas are inline JSON Schema strings wrapped in `BinaryData.FromString()`.
- The `Agent` supports configurable `ChatReasoningEffortLevel` for models that support reasoning.
- LM Studio endpoint is hardcoded in `Program.cs:12` (`http://192.168.178.59:1234/v1`).

## Security Notes

Loading any DLL from `~/.agentic/tools/` at startup is implicit trust — anyone who can write to that folder can execute code in the Agentic process. The factory's static analysis (forbidden APIs + namespace allowlist) only constrains what the *coder LLM* generates; it does not validate DLLs that appear in the folder by other means.
