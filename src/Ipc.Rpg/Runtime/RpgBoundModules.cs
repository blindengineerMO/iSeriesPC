using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private RpgRuntimeContext? _bindingRoot;
    private Dictionary<RpgProgram, RpgRuntimeContext> _boundModuleContexts = new();
    private RpgRuntimeContext BindingRoot => _bindingRoot ?? GlobalContext?.BindingRoot ?? this;
    internal RpgRuntimeContext BoundModuleContext(RpgProgram program, RpgHost host)
    {
        if (ReferenceEquals(ModuleContext.Program, program)) return ModuleContext;
        var root = BindingRoot;
        if (ReferenceEquals(root.Program, program)) return root;
        if (!root._boundModuleContexts.TryGetValue(program, out var context))
            root._boundModuleContexts.Add(program, context = new(program, host, null, root._exportStorage) { _bindingRoot = root });
        context.RebindHost(host);
        return context;
    }
}
