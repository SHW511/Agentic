using System.Runtime.Loader;

namespace Agentic.Core;

/// <summary>
/// Persisted metadata for a dynamic tool. Lives next to its compiled DLL on disk.
/// </summary>
public record ToolMeta(
    string Name,
    string Description,
    string ParameterSchema,
    string CoderModel,
    string SourceIntent,
    string CoreAssemblyVersion,
    string SourceSha256,
    DateTimeOffset CreatedAt);

/// <summary>
/// Result of a successful Roslyn compile, before the user has approved it.
/// </summary>
public record ToolProposal(
    ITool Tool,
    string CSharpSource,
    ToolMeta Meta,
    AssemblyLoadContext LoadContext);

/// <summary>
/// A dynamic tool that has been compiled and persisted to its own folder under the tools root.
/// </summary>
public record PersistedTool(
    ITool Tool,
    string FolderPath,
    ToolMeta Meta,
    AssemblyLoadContext LoadContext);
