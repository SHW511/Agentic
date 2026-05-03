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
        const string chatModelName  = "nvidia/nemotron-3-nano-omni";
        // Coder model used by ToolFactory. Must be loaded in LM Studio alongside the chat model.
        // If unsure of the exact id, run: curl http://192.168.178.59:1234/v1/models
        const string coderModelName = "qwen2.5-coder-7b-instruct";

        var toolsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentic", "tools");

        // --- LLM Clients ---
        var client = new OpenAIClient(
            new ApiKeyCredential("lm-studio"),
            new OpenAIClientOptions { Endpoint = new Uri(lmStudioEndpoint) });

        var chatClient  = client.GetChatClient(chatModelName);
        var coderClient = client.GetChatClient(coderModelName);

        // --- Built-in tools ---
        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new WriteFileTool());
        tools.Register(new ListDirectoryTool());
        tools.Register(new RunCommandTool());
        tools.Register(new WebSearchTool());
        tools.Register(new WebFetchTool());

        // --- Tool factory + meta-tools ---
        var factory = new ToolFactory(coderClient, coderModelName, toolsRoot);

        tools.Register(new RequestNewToolTool(factory, tools, ApproveProposalAtRepl));
        tools.Register(new ListDynamicToolsTool(tools));
        tools.Register(new UnregisterToolTool(tools, ApproveDeleteAtRepl));

        // --- Load any previously-persisted dynamic tools ---
        foreach (var folder in Directory.EnumerateDirectories(toolsRoot))
        {
            try
            {
                var pt = factory.LoadFromDisk(folder);
                tools.RegisterPersisted(pt);
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  [loaded dynamic tool] {pt.Tool.Name}");
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [skip dynamic tool at {folder}] {ex.Message}");
                Console.ResetColor();
            }
        }

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

        If no existing tool fits the user's request, you may call `request_new_tool`
        with a clear `intent` describing what the tool should do, its inputs, and its
        outputs. The user will be asked to approve the generated tool. After approval
        succeeds, the new tool will be available on your next turn — call it then.
        Prefer existing tools over requesting new ones.

        The user is on Windows. Use cmd syntax for shell commands.
        """,
            chatClient: chatClient,
            tools: tools,
            reasoningEffort: ChatReasoningEffortLevel.Medium);

        // --- REPL ---
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Agentic - Local AI Assistant");
        Console.WriteLine($"Chat model:  {chatModelName}");
        Console.WriteLine($"Coder model: {coderModelName}");
        Console.WriteLine($"Endpoint:    {lmStudioEndpoint}");
        Console.WriteLine($"Tools root:  {toolsRoot}");
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

    private static Task<bool> ApproveProposalAtRepl(ToolProposal proposal)
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(new string('═', 70));
        Console.WriteLine("Proposed new tool");
        Console.WriteLine(new string('═', 70));
        Console.ResetColor();
        Console.WriteLine($"Name:        {proposal.Meta.Name}");
        Console.WriteLine($"Description: {proposal.Meta.Description}");
        Console.WriteLine($"Schema:      {proposal.Meta.ParameterSchema}");
        Console.WriteLine($"Source ({proposal.CSharpSource.Split('\n').Length} lines):");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(proposal.CSharpSource);
        Console.ResetColor();
        Console.WriteLine(new string('─', 70));
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("Accept and persist this tool? [y/N]: ");
        Console.ResetColor();
        var answer = Console.ReadLine()?.Trim();
        return Task.FromResult(answer is not null &&
            (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
             answer.Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }

    private static Task<bool> ApproveDeleteAtRepl(string toolName)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"Also delete the on-disk folder for '{toolName}'? [y/N]: ");
        Console.ResetColor();
        var answer = Console.ReadLine()?.Trim();
        return Task.FromResult(answer is not null &&
            (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
             answer.Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }
}
