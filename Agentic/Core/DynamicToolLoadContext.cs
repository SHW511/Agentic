using System.Reflection;
using System.Runtime.Loader;

namespace Agentic.Core;

/// <summary>
/// Collectible AssemblyLoadContext for a single dynamic tool, so the assembly
/// can be unloaded when the tool is unregistered or regenerated.
/// </summary>
public sealed class DynamicToolLoadContext : AssemblyLoadContext
{
    public DynamicToolLoadContext(string name) : base(name, isCollectible: true) { }

    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
