using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using OpenAI.Chat;

namespace Agentic.Core;

/// <summary>
/// Orchestrates the "coder LLM proposes → Roslyn compiles → DLL is loaded" pipeline.
/// </summary>
public class ToolFactory
{
    private readonly ChatClient _coder;
    private readonly string _coderModelName;
    private readonly string _toolsRoot;
    private readonly int _maxRetries;

    private static readonly HashSet<string> AllowedNamespacePrefixes = new(StringComparer.Ordinal)
    {
        "System",
        "System.IO",
        "System.Text",
        "System.Text.Json",
        "System.Text.RegularExpressions",
        "System.Diagnostics",
        "System.Net.Http",
        "System.Net.Http.Json",
        "System.Security.Cryptography",
        "System.Threading",
        "System.Threading.Tasks",
        "System.Collections",
        "System.Collections.Generic",
        "System.Linq",
        "Agentic.Core",
    };

    private static readonly string[] ForbiddenSubstrings =
    {
        "System.Reflection.Emit",
        "System.Runtime.InteropServices",
        "AppDomain",
        "Assembly.Load",
        "Activator.CreateInstance",
        "DllImport",
    };

    public ToolFactory(ChatClient coder, string coderModelName, string toolsRoot, int maxRetries = 3)
    {
        _coder = coder;
        _coderModelName = coderModelName;
        _toolsRoot = toolsRoot;
        _maxRetries = maxRetries;
        Directory.CreateDirectory(_toolsRoot);
    }

    public string ToolsRoot => _toolsRoot;

    /// <summary>
    /// Ask the coder model for a tool that satisfies <paramref name="intent"/>, then compile and instantiate it.
    /// Retries on JSON parse / compile failure, feeding diagnostics back to the model.
    /// Does NOT persist anything yet — call <see cref="PersistAsync"/> after user approval.
    /// </summary>
    public async Task<ToolProposal> ProposeAsync(string intent, CancellationToken ct = default)
    {
        var history = new List<ChatMessage>
        {
            ChatMessage.CreateSystemMessage(SystemPrompt),
            ChatMessage.CreateUserMessage($"Intent: {intent}"),
        };

        Exception? lastError = null;

        for (int attempt = 1; attempt <= _maxRetries; attempt++)
        {
            ChatCompletion completion = await _coder.CompleteChatAsync(history, new ChatCompletionOptions(), ct);
            var raw = string.Join("", completion.Content
                .Where(c => c.Kind == ChatMessageContentPartKind.Text)
                .Select(c => c.Text));

            history.Add(ChatMessage.CreateAssistantMessage(raw));

            try
            {
                var envelope = ParseEnvelope(raw);
                RejectForbiddenApis(envelope.CSharpSource);
                var (assembly, alc) = Compile(envelope.Name, envelope.CSharpSource);
                var tool = Instantiate(assembly, envelope);
                var meta = BuildMeta(envelope, intent);
                return new ToolProposal(tool, envelope.CSharpSource, meta, alc);
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt == _maxRetries) break;
                history.Add(ChatMessage.CreateUserMessage(
                    $"Your previous response failed:\n\n{ex.Message}\n\nReturn ONLY the corrected JSON envelope. No prose."));
            }
        }

        throw new InvalidOperationException(
            $"ToolFactory failed after {_maxRetries} attempts. Last error: {lastError?.Message}", lastError);
    }

    /// <summary>
    /// Persist an approved proposal: write tool.dll + tool.pdb + source.cs + meta.json under a per-tool folder.
    /// Returns the same tool instance, repackaged as a <see cref="PersistedTool"/>.
    /// </summary>
    public async Task<PersistedTool> PersistAsync(ToolProposal proposal, CancellationToken ct = default)
    {
        var folder = Path.Combine(_toolsRoot, proposal.Meta.Name);
        Directory.CreateDirectory(folder);

        await File.WriteAllTextAsync(Path.Combine(folder, "source.cs"), proposal.CSharpSource, ct);
        await File.WriteAllTextAsync(Path.Combine(folder, "meta.json"),
            JsonSerializer.Serialize(proposal.Meta, new JsonSerializerOptions { WriteIndented = true }), ct);

        // Re-emit DLL+PDB to disk. The proposal already compiled, so this should always succeed.
        var (dllBytes, pdbBytes) = EmitToBytes(proposal.Meta.Name, proposal.CSharpSource);
        await File.WriteAllBytesAsync(Path.Combine(folder, "tool.dll"), dllBytes, ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, "tool.pdb"), pdbBytes, ct);

        return new PersistedTool(proposal.Tool, folder, proposal.Meta, proposal.LoadContext);
    }

    /// <summary>
    /// Load a previously-persisted tool from disk. If the DLL fails to load (e.g. Agentic.Core version
    /// mismatch), recompile from source.cs.
    /// </summary>
    public PersistedTool LoadFromDisk(string folder)
    {
        var meta = JsonSerializer.Deserialize<ToolMeta>(File.ReadAllText(Path.Combine(folder, "meta.json")))
            ?? throw new InvalidOperationException($"meta.json malformed in {folder}");

        var alc = new DynamicToolLoadContext(meta.Name);
        Assembly assembly;
        var dllPath = Path.Combine(folder, "tool.dll");

        try
        {
            using var fs = File.OpenRead(dllPath);
            assembly = alc.LoadFromStream(fs);
        }
        catch
        {
            // Fall back to recompiling from source.
            var src = File.ReadAllText(Path.Combine(folder, "source.cs"));
            var (recompiledDll, recompiledPdb) = EmitToBytes(meta.Name, src);
            File.WriteAllBytes(dllPath, recompiledDll);
            File.WriteAllBytes(Path.Combine(folder, "tool.pdb"), recompiledPdb);
            using var fs = File.OpenRead(dllPath);
            assembly = alc.LoadFromStream(fs);
        }

        var tool = Instantiate(assembly, new Envelope(meta.Name, meta.Description, meta.ParameterSchema, ""));
        return new PersistedTool(tool, folder, meta, alc);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private record Envelope(string Name, string Description, string ParameterSchema, string CSharpSource);

    private static Envelope ParseEnvelope(string raw)
    {
        // The model emits two fenced blocks: a ```json metadata block and a ```csharp source block.
        // This avoids the JSON-in-JSON escaping nightmare that small models can't reliably handle.
        var jsonBlock = ExtractFencedBlock(raw, "json")
            ?? throw new InvalidOperationException(
                "could not find ```json metadata block. Output two fenced blocks: ```json {...} ``` then ```csharp ... ```.");

        var csharpBlock = ExtractFencedBlock(raw, "csharp")
            ?? ExtractFencedBlock(raw, "cs")
            ?? throw new InvalidOperationException(
                "could not find ```csharp source block. Output two fenced blocks: ```json {...} ``` then ```csharp ... ```.");

        using var doc = JsonDocument.Parse(jsonBlock);
        var root = doc.RootElement;

        var name = root.GetProperty("name").GetString()
            ?? throw new InvalidOperationException("metadata.name missing");
        var description = root.GetProperty("description").GetString()
            ?? throw new InvalidOperationException("metadata.description missing");
        var schema = root.GetProperty("parameter_schema").GetRawText();

        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]*$"))
            throw new InvalidOperationException($"name must be snake_case: '{name}'");

        var source = NormalizeSource(csharpBlock);
        return new Envelope(name, description, schema, source);
    }

    private static string? ExtractFencedBlock(string raw, string lang)
    {
        // Find ```<lang> ... ```
        var pattern = $"```{lang}\\s*\\r?\\n(.*?)```";
        var match = System.Text.RegularExpressions.Regex.Match(
            raw, pattern,
            System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return match.Groups[1].Value.Trim();
    }

    /// <summary>
    /// Auto-inject the standard usings the model usually forgets, and strip any redundant ones already present.
    /// Also strips any `namespace` declaration so the tool type ends up in the global namespace
    /// (where Instantiate looks for it via assembly.GetTypes()).
    /// </summary>
    private static string NormalizeSource(string source)
    {
        var requiredUsings = new[]
        {
            "using System;",
            "using System.IO;",
            "using System.Text;",
            "using System.Text.Json;",
            "using System.Threading;",
            "using System.Threading.Tasks;",
            "using System.Collections.Generic;",
            "using System.Linq;",
            "using Agentic.Core;",
        };

        // Drop any leading usings the model already wrote — we'll re-add ours.
        var lines = source.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 &&
               (string.IsNullOrWhiteSpace(lines[0]) ||
                lines[0].TrimStart().StartsWith("using ", StringComparison.Ordinal)))
        {
            lines.RemoveAt(0);
        }

        var body = string.Join("\n", lines);
        return string.Join("\n", requiredUsings) + "\n\n" + body;
    }

    private static void RejectForbiddenApis(string source)
    {
        foreach (var f in ForbiddenSubstrings)
            if (source.Contains(f, StringComparison.Ordinal))
                throw new InvalidOperationException($"forbidden API used: {f}");

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var ns = u.Name?.ToString() ?? "";
            if (string.IsNullOrEmpty(ns)) continue;
            if (!AllowedNamespacePrefixes.Any(allowed =>
                    ns == allowed || ns.StartsWith(allowed + ".", StringComparison.Ordinal)))
                throw new InvalidOperationException($"using directive not allowed: {ns}");
        }
    }

    private (Assembly Assembly, AssemblyLoadContext Context) Compile(string toolName, string source)
    {
        var (dll, _) = EmitToBytes(toolName, source);
        var alc = new DynamicToolLoadContext(toolName);
        using var ms = new MemoryStream(dll);
        var asm = alc.LoadFromStream(ms);
        return (asm, alc);
    }

    private (byte[] Dll, byte[] Pdb) EmitToBytes(string toolName, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));
        var compilation = CSharpCompilation.Create(
            assemblyName: $"Agentic.Dynamic.{toolName}",
            syntaxTrees: new[] { tree },
            references: BuildReferenceSet(),
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                allowUnsafe: false));

        using var dll = new MemoryStream();
        using var pdb = new MemoryStream();
        var emit = compilation.Emit(dll, pdb,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));

        if (!emit.Success)
        {
            var diags = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString());
            throw new InvalidOperationException("compile failed:\n" + string.Join("\n", diags));
        }

        return (dll.ToArray(), pdb.ToArray());
    }

    private static IEnumerable<MetadataReference> BuildReferenceSet()
    {
        var trustedAssemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var wanted = new[]
        {
            "System.Private.CoreLib",
            "System.Runtime",
            "System.Console",
            "System.Linq",
            "System.Collections",
            "System.IO.FileSystem",
            "System.Text.Json",
            "System.Text.RegularExpressions",
            "System.Net.Http",
            "System.Net.Primitives",
            "System.Memory",
            "System.Memory.Data",
            "System.Threading",
            "System.Threading.Tasks",
            "System.Diagnostics.Process",
            "System.Security.Cryptography",
            "System.ObjectModel",
            "netstandard",
        };

        foreach (var path in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (wanted.Contains(name))
                yield return MetadataReference.CreateFromFile(path);
        }

        // Reference the running Agentic.Core (i.e. this assembly) so generated tools can implement ITool.
        yield return MetadataReference.CreateFromFile(typeof(ITool).Assembly.Location);
        // Belt-and-braces: reference the actual assembly that defines BinaryData, in case
        // System.Memory.Data isn't in the trusted-platform-assemblies list under that name.
        yield return MetadataReference.CreateFromFile(typeof(BinaryData).Assembly.Location);
    }

    private static ITool Instantiate(Assembly assembly, Envelope envelope)
    {
        var toolType = assembly.GetTypes()
            .FirstOrDefault(t => typeof(ITool).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            ?? throw new InvalidOperationException("no concrete ITool implementation found in compiled assembly");

        var ctor = toolType.GetConstructor(Type.EmptyTypes)
            ?? throw new InvalidOperationException($"{toolType.FullName} must have a parameterless constructor");

        var tool = (ITool)ctor.Invoke(null);
        if (tool.Name != envelope.Name)
            throw new InvalidOperationException(
                $"compiled tool name '{tool.Name}' does not match envelope name '{envelope.Name}'");
        return tool;
    }

    private ToolMeta BuildMeta(Envelope env, string intent)
    {
        var coreVersion = typeof(ITool).Assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(env.CSharpSource)));
        return new ToolMeta(
            Name: env.Name,
            Description: env.Description,
            ParameterSchema: env.ParameterSchema,
            CoderModel: _coderModelName,
            SourceIntent: intent,
            CoreAssemblyVersion: coreVersion,
            SourceSha256: hash,
            CreatedAt: DateTimeOffset.UtcNow);
    }

    private const string SystemPrompt = """"
You author C# tool implementations for the Agentic framework.

Output EXACTLY two fenced code blocks and nothing else. No prose before, between, or after.

First block: ```json containing the metadata object.
Second block: ```csharp containing the complete tool source.

The metadata object has these keys:
  - name: snake_case identifier, e.g. "count_text_stats"
  - description: one sentence, imperative voice
  - parameter_schema: a JSON Schema object (type: "object", with "properties" and "required")

The C# source MUST:
  - Define exactly one public class that implements Agentic.Core.ITool
  - Have a public parameterless constructor (or no constructor at all)
  - Be in the GLOBAL namespace (do NOT wrap in `namespace ... { }`)
  - Implement these four members:
      public string Name => "...";
      public string Description => "...";
      public BinaryData ParameterSchema => BinaryData.FromString(SCHEMA_JSON_STRING);
      public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
  - Make Name return EXACTLY the metadata.name value
  - Return errors via `new ToolResult(message, IsError: true)` — do NOT throw
  - You do NOT need to write `using` directives. They are auto-injected. Just write the class.

Use `BinaryData.FromString(@"...")` with a verbatim string literal for the schema, doubling any " inside.
Or build the schema with raw string literals: `BinaryData.FromString("""{...}""")`.

Allowed namespaces (auto-imported): System, System.IO, System.Text, System.Text.Json,
System.Text.RegularExpressions, System.Diagnostics, System.Net.Http, System.Net.Http.Json,
System.Threading, System.Threading.Tasks, System.Collections, System.Collections.Generic,
System.Linq, System.Security.Cryptography, Agentic.Core.

Forbidden: reflection emit, P/Invoke, AppDomain, Assembly.Load, dynamic codegen.

Here is a complete worked example. Match this format exactly.

```json
{
  "name": "count_text_stats",
  "description": "Count lines, words, and characters in a text file.",
  "parameter_schema": {
    "type": "object",
    "properties": {
      "path": { "type": "string", "description": "Absolute path to the text file." }
    },
    "required": ["path"],
    "additionalProperties": false
  }
}
```

```csharp
public class CountTextStatsTool : ITool
{
    public string Name => "count_text_stats";
    public string Description => "Count lines, words, and characters in a text file.";

    public BinaryData ParameterSchema => BinaryData.FromString(@"{
        ""type"": ""object"",
        ""properties"": {
            ""path"": { ""type"": ""string"", ""description"": ""Absolute path to the text file."" }
        },
        ""required"": [""path""],
        ""additionalProperties"": false
    }");

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(path))
            return new ToolResult("path must be a non-empty string", IsError: true);
        if (!File.Exists(path))
            return new ToolResult($"File not found: {path}", IsError: true);

        var text = await File.ReadAllTextAsync(path, ct);
        var lines = text.Split('\n').Length;
        var words = text.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        var chars = text.Length;

        return new ToolResult($"lines={lines} words={words} chars={chars}");
    }
}
```
"""";
}
