using OpenAI.Chat;

namespace Agentic.Core;

public class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new();
    private readonly Dictionary<string, PersistedTool> _persisted = new();

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
    }

    public void RegisterPersisted(PersistedTool pt)
    {
        _tools[pt.Tool.Name] = pt.Tool;
        _persisted[pt.Tool.Name] = pt;
    }

    /// <summary>
    /// Remove a tool. If it was persisted, unload its AssemblyLoadContext so the DLL can be
    /// regenerated or deleted. Returns the persisted record if there was one (so the caller
    /// can decide whether to delete the on-disk folder).
    /// </summary>
    public PersistedTool? Unregister(string name)
    {
        _tools.Remove(name);
        if (!_persisted.Remove(name, out var pt)) return null;

        pt.LoadContext.Unload();
        // Force collection so the DLL file becomes deletable on Windows.
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return pt;
    }

    public ITool? Get(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyCollection<ITool> All => _tools.Values.ToList().AsReadOnly();

    public IReadOnlyCollection<PersistedTool> AllPersisted => _persisted.Values.ToList().AsReadOnly();

    public IList<ChatTool> ToChatTools() =>
        _tools.Values
            .Select(t => ChatTool.CreateFunctionTool(t.Name, t.Description, t.ParameterSchema))
            .ToList();
}
