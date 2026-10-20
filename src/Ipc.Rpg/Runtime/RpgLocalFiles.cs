using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private Dictionary<RpgProgram, RpgDiskState> _procedureFiles = new();
    private Dictionary<RpgProgram, RpgWorkstationState> _procedureWorkstations = new();
    internal RpgWorkstationState WorkstationStateFor(string name)
    {
        var file = _program.Workstations.SingleOrDefault(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (file?.Request.Procedure is null) return ModuleContext._workstationState;
        if (!file.Request.Static) return _workstationState;
        if (!ModuleContext._procedureWorkstations.TryGetValue(_program, out var state)) ModuleContext._procedureWorkstations.Add(_program, state = new());
        return state;
    }
    internal RpgDiskState DiskStateFor(string name)
    {
        var file = _program.DiskFiles.SingleOrDefault(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (file?.Request.Procedure is null) return ModuleContext._diskState;
        if (!file.Request.Static) return _diskState;
        if (!ModuleContext._procedureFiles.TryGetValue(_program, out var state))
            ModuleContext._procedureFiles.Add(_program, state = new());
        return state;
    }
    private Dictionary<string, FileStatus> StatusFor(string name) => _program.Workstations.Any(f => f.Request.Name == name && f.Request.Procedure is not null)
        ? WorkstationStateFor(name).Status : _program.DiskFiles.Any(f => f.Request.Name == name && f.Request.Procedure is not null)
        ? DiskStateFor(name).Status : GlobalContext?.StatusFor(name) ?? _fileStatus;
    private Dictionary<string, int> NumericStatusFor(string name) => _program.Workstations.Any(f => f.Request.Name == name && f.Request.Procedure is not null)
        ? WorkstationStateFor(name).NumericStatus : _program.DiskFiles.Any(f => f.Request.Name == name && f.Request.Procedure is not null)
        ? DiskStateFor(name).NumericStatus : GlobalContext?.NumericStatusFor(name) ?? _numericFileStatus;
    internal void CloseAutomaticFiles() { try { _diskState.Dispose(); } finally { _workstationState.Dispose(); } }

    /// <summary>Restart cycle storage while preserving procedure STATIC storage for the activation.</summary>
    public RpgRuntimeContext RestartCycle()
    {
        if (GlobalContext is not null) throw new InvalidOperationException("Only the module cycle can be restarted.");
        try { CloseWorkstations(includeProcedureStatic: false); } finally { CloseDiskFiles(includeProcedureStatic: false); }
        var restarted = new RpgRuntimeContext(_program, _host, null, _exportStorage) { _boundModuleContexts = _boundModuleContexts, _procedureFiles = _procedureFiles, _procedureWorkstations = _procedureWorkstations, _procedureStaticStorage = _procedureStaticStorage,
            _procedureStaticBytes = _procedureStaticBytes, _staticNullIndicators = _staticNullIndicators };
        foreach (var module in _boundModuleContexts.Values) module._bindingRoot = restarted;
        _exportStorage.ProcedureRoot = restarted;
        return restarted;
    }
}

public sealed partial class RpgInterpreter
{
    private void InitializeProcedureFiles(RpgRuntimeContext context)
    {
        foreach (var name in context.Program.FileSpecs.AsEnumerable().Reverse())
        {
            var file = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name && f.Request.Procedure == context.Program.NativeProcedureName);
            var display = context.Program.Workstations.SingleOrDefault(f => f.Request.Name == name && f.Request.Procedure == context.Program.NativeProcedureName);
            if (file is null && display is null || file?.Request.IsTemplate == true || file?.Request.IsParameter == true || display?.Request.IsTemplate == true || display?.Request.IsParameter == true) continue;
            var line = context.Program.FileDeclarationLines.GetValueOrDefault(name);
            var opening = new RpgStatement { Opcode = RpgOpcode.Open, Factor2 = name, LineNumber = line,
                Location = line > 0 && line <= context.Program.Source!.Locations.Count ? context.Program.Source.Locations[line - 1] : null };
            context.SetFeedbackOperation(opening, context.Program.NativeProcedureName ?? "*INIT");
            try
            {
                if (display is not null)
                {
                    var displays = context.WorkstationStateFor(name);
                    if (displays.Started.Contains(name))
                    { if (displays.Files.TryGetValue(name, out var retainedDisplay)) _host.RebindWorkstation?.Invoke(retainedDisplay); continue; }
                    if (!display.Request.UserOpen)
                    { displays.Files.Add(name, ImplicitFile(name, () => CreateWorkstation(display))); context.SetFileStatus(name, clearFileEof: true); }
                    displays.Started.Add(name); continue;
                }
                var state = context.DiskStateFor(name);
                if (state.Started.Contains(name))
                {
                    if (state.Files.TryGetValue(name, out var retained))
                        FileIo(name, () => { retained.Rebind(_host.CancellationToken); RpgDiskLayouts.Validate(file!, retained.Cursor); }, 1216);
                    continue;
                }
                if (!file!.Request.UserOpen)
                { state.Files.Add(name, ImplicitFile(name, () => OpenWithRecno(context, name, () => CreateDisk(file)))); context.SetFileStatus(name, clearFileEof: true); }
                state.Started.Add(name);
            }
            catch (RpgRuntimeException error) when (error.FileError)
            {
                context.RecordStatus(error.StatusCode ?? 1216, name); context.RecordFeedbackError(error);
                if (InvokeErrorSubroutine(error, context)) return;
                throw Located(error, opening);
            }
        }
    }
}
