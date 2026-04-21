using OpenAI.Chat;

namespace Agentic.Core;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyCollection<ITool> All => _tools.Values.ToList().AsReadOnly();

    public IList<ChatTool> ToChatTools() =>
        _tools.Values
            .Select(t => ChatTool.CreateFunctionTool(t.Name, t.Description, t.ParameterSchema))
            .ToList();
}
