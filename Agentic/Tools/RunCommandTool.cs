using System.Diagnostics;
using System.Text.Json;
using Agentic.Core;

namespace Agentic.Tools;

public class RunCommandTool : ITool
{
    public string Name => "run_command";
    public string Description => "Execute a terminal command and return its stdout and stderr.";

    public BinaryData ParameterSchema => BinaryData.FromString("""
    {
        "type": "object",
        "properties": {
            "command": {
                "type": "string",
                "description": "The terminal command to execute."
            },
            "working_directory": {
                "type": "string",
                "description": "Optional working directory for the command. Defaults to the current directory."
            }
        },
        "required": ["command"],
        "additionalProperties": false
    }
    """);

    public async Task<ToolResult> ExecuteAsync(JsonElement arguments, CancellationToken ct = default)
    {
        var command = arguments.GetProperty("command").GetString()!;
        var workDir = arguments.TryGetProperty("working_directory", out var wd) ? wd.GetString() : null;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd",
            Arguments = $"/c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir))
            psi.WorkingDirectory = workDir;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = stdout;
        if (!string.IsNullOrWhiteSpace(stderr))
            output += $"\n[STDERR]\n{stderr}";

        if (process.ExitCode != 0)
            output += $"\n[EXIT CODE: {process.ExitCode}]";

        return new ToolResult(output, IsError: process.ExitCode != 0);
    }
}
