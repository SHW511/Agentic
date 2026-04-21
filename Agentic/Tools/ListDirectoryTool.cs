using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class ListDirectoryTool : ITool
{
    public string Name => "list_directory";
    public string Description => "List files and subdirectories in a directory. Shows item counts for subdirectories. When subdirectories contain items, ask the user if they want you to list those too.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "path": {
                "type": "string",
                "description": "The absolute path to the directory to list."
            },
            "recursive": {
                "type": "boolean",
                "description": "If true, list all contents recursively including subdirectories. Defaults to false."
            }
        },
        "required": ["path"],
        "additionalProperties": false
    }
    """);

    public Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var path = arguments.GetProperty("path").GetString()!;
        var recursive = arguments.TryGetProperty("recursive", out var rec) && rec.GetBoolean();

        if (!Directory.Exists(path))
            return Task.FromResult(new ToolResult($"Directory not found: {path}", IsError: true));

        var output = recursive
            ? BuildRecursiveListing(path, indent: 0)
            : BuildShallowListing(path);

        return Task.FromResult(new ToolResult(output));
    }

    private static string BuildShallowListing(string path)
    {
        var entries = new List<string>();

        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir);
            var subDirs = Directory.GetDirectories(dir).Length;
            var subFiles = Directory.GetFiles(dir).Length;
            var total = subDirs + subFiles;
            var summary = total > 0 ? $"({subFiles} files, {subDirs} folders)" : "(empty)";
            entries.Add($"[DIR]  {name}  {summary}");
        }

        foreach (var file in Directory.GetFiles(path))
        {
            var info = new FileInfo(file);
            entries.Add($"       {info.Name}  ({FormatSize(info.Length)})");
        }

        return entries.Count > 0
            ? string.Join(Environment.NewLine, entries)
            : "(empty directory)";
    }

    private static string BuildRecursiveListing(string path, int indent)
    {
        var prefix = new string(' ', indent * 2);
        var entries = new List<string>();

        foreach (var dir in Directory.GetDirectories(path))
        {
            var name = Path.GetFileName(dir);
            entries.Add($"{prefix}[DIR] {name}/");
            entries.Add(BuildRecursiveListing(dir, indent + 1));
        }

        foreach (var file in Directory.GetFiles(path))
        {
            var info = new FileInfo(file);
            entries.Add($"{prefix}      {info.Name}  ({FormatSize(info.Length)})");
        }

        return string.Join(Environment.NewLine, entries);
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };
}
