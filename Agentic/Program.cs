using System.ClientModel;
using OpenAI;
using OpenAI.Chat;
using Agentic.Core;
using Agentic.Tools;

internal class Program
{
    private static async Task Main(string[] args)
    {
        // --- Configuration ---
        const string lmStudioEndpoint = "http://192.168.178.59:1234/v1";
        const string modelName = "openai/gpt-oss-20b"; // Leave empty — LM Studio uses whatever model is loaded

        // --- LLM Client ---
        var client = new OpenAIClient(
            new ApiKeyCredential("lm-studio"),
            new OpenAIClientOptions { Endpoint = new Uri(lmStudioEndpoint) });

        var chatClient = client.GetChatClient(modelName);

        // --- Tools ---
        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new WriteFileTool());
        tools.Register(new ListDirectoryTool());
        tools.Register(new RunCommandTool());
        tools.Register(new WebSearchTool());
        tools.Register(new WebFetchTool());

        // --- Agent ---
        var agent = new Agent(
            name: "Assistant",
            systemPrompt: """
        You are a helpful AI assistant that can interact with the user's local system.
        You have tools to read files, write files, list directories, run shell commands,
        search the web, and fetch web pages.

        When the user asks you to do something:
        - Use your tools to accomplish the task.
        - Be concise in your responses.
        - If a task requires multiple steps, use multiple tool calls.
        - Always confirm what you did after completing an action.

        The user is on Windows. Use PowerShell syntax for shell commands.
        """,
            chatClient: chatClient,
            tools: tools,
            reasoningEffort: ChatReasoningEffortLevel.High);

        // --- REPL ---
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Agentic - Local AI Assistant");
        Console.WriteLine("Connected to LM Studio at " + lmStudioEndpoint);
        Console.WriteLine("Type 'exit' to quit, 'reset' to clear conversation history.");
        Console.WriteLine(new string('─', 50));
        Console.ResetColor();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        while (!cts.Token.IsCancellationRequested)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("> ");
            Console.ResetColor();

            var input = Console.ReadLine();
            if (input is null) break;

            var trimmed = input.Trim();
            if (trimmed.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            if (trimmed.Equals("reset", StringComparison.OrdinalIgnoreCase))
            {
                agent.Reset();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Conversation reset.");
                Console.ResetColor();
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed)) continue;

            try
            {
                var response = await agent.RunAsync(trimmed, cts.Token);

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine(response);
                Console.ResetColor();
                Console.WriteLine();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("\nCancelled.");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }
}