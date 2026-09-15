using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Cl.Interpreter;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Rpg.Runtime;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterEnvironmentCommands()
    {
        _catalog.Register("OVRDBF", ExecuteEnvironment);
        _catalog.Register("DLTOVR", ExecuteEnvironment);
        _catalog.Register("DSPOVR", ExecuteEnvironment);
        _catalog.Register("CHGDTAARA", ExecuteJobDataArea);
        _catalog.Register("DSPDTAARA", ExecuteJobDataArea);
        _catalog.Register("RTVJOBA", _ => CommandResult.Error("RTVJOBA requires a compiled CL variable frame."));
        _catalog.Register("RTVDTAARA", _ => CommandResult.Error("RTVDTAARA requires a compiled CL variable frame."));
        _catalog.Register("CRTDTAARA", CreateDataArea);
        _catalog.Register("DLTDTAARA", DeleteDataArea);
        RegisterMessageCommands();
    }
    private JobCallEnvironment EnvironmentForJob() => _job is null ? throw new CpfException("CPF1241", "An executing job is required.") :
        _system.JobRuntime.Environment(_job.Key) ?? throw new CpfException("CPF1241", "Job has no runtime owner.");
    private CommandResult ExecuteEnvironment(CommandCall call)
    {
        var environment = EnvironmentForJob();
        var file = CommandParser.Unquote(call.GetOption("FILE") ?? "*ALL").ToUpperInvariant();
        if (call.Name.Equals("DSPOVR", StringComparison.OrdinalIgnoreCase))
            return new CommandResult { Listing = environment.Overrides().Where(x => file == "*ALL" || x.File == file)
                .Select(x => $"{x.File} TOFILE({x.Library}/{x.Name}) MBR({x.Member}) SHARE({(x.Share ? "*YES" : "*NO")}) OVRSCOPE({(x.Scope == JobEnvironmentScope.Job ? "*JOB" : "*CALLLVL")})").ToArray() };
        var scopeText = CommandParser.Unquote(call.GetOption("OVRSCOPE") ?? call.GetOption("LVL") ?? "*CALLLVL").ToUpperInvariant();
        var scope = scopeText switch { "*JOB" => JobEnvironmentScope.Job, "*CALLLVL" or "*" => JobEnvironmentScope.Call,
            _ => throw new CpfException("IPC0126", "Supported override scopes are *CALLLVL and *JOB.") };
        if (call.Name.Equals("DLTOVR", StringComparison.OrdinalIgnoreCase)) environment.DeleteOverride(file, scope);
        else
        {
            var target = CommandParser.Unquote(call.GetOption("TOFILE") ?? file).ToUpperInvariant();
            var (library, name) = ResolveWorkObject(target, ObjectType.File);
            _ = _system.Objects.GetRequired(library, name, ObjectType.File);
            var member = CommandParser.Unquote(call.GetOption("MBR") ?? "*FIRST").ToUpperInvariant();
            if (member == "*FIRST") member = _files.FirstMember(library, name);
            if (!_files.MemberExists(library, name, member)) throw new CpfException("CPF2817", "Override member not found.");
            var share = CommandParser.Unquote(call.GetOption("SHARE") ?? "*NO").ToUpperInvariant() switch {
                "*YES" => true, "*NO" => false, _ => throw new CpfException("IPC0126", "SHARE must be *YES or *NO.") };
            environment.Override(new(file, library, name, member, share, scope));
        }
        return CommandResult.Ok("Job file overrides updated.");
    }
    private CommandResult ExecuteJobDataArea(CommandCall call) => ExecuteJobDataArea(call, (ClCommandContext?)null);
    private CommandResult ExecuteJobDataArea(CommandCall call, ClCommandContext? context)
    {
        if (call.Name.Equals("DSPDTAARA", StringComparison.OrdinalIgnoreCase))
        {
            var found = ReadDataArea(call, context);
            var displayText = found.Value is Ipc.Core.Work.ProgramBuffer buffer ? DisplayEncodedText(buffer) : ClExpression.Text(found.Value);
            var displaySelection = DataAreaSelection(call, context?.Resolve);
            return new CommandResult { Listing = displaySelection.Name.StartsWith('*') ? new[] { displayText } : new[] {
                "Data area " + displaySelection.Name, $"Type: {found.Type}  Length: {found.Length}  Decimals: {found.Decimals}  CCSID: {found.Ccsid}",
                "Text: " + found.Text, displayText } };
        }
        var selection = DataAreaSelection(call, context?.Resolve);
        var valueObject = DataAreaInput(call.GetOption("VALUE") ?? throw new CpfException("IPC0126", "VALUE is required."), context);
        if (!selection.Name.StartsWith('*'))
        {
            var (library, name) = ResolveWorkObject(selection.Name, ObjectType.DataArea);
            new DataAreaStore(_system.Connections).Change(library, name, valueObject, selection.Start, selection.Length);
            return CommandResult.Ok("Data area changed.");
        }
        var (data, start, length, area) = SelectJobDataArea(call, context?.Resolve);
        var store = new JobDataAreaStore(_system.Connections);
        var encoding = (System.Text.Encoding)CodePage.FromCcsid(_job!.Ccsid).Clone();
        encoding.EncoderFallback = System.Text.EncoderFallback.ExceptionFallback; encoding.DecoderFallback = System.Text.DecoderFallback.ExceptionFallback;
        var value = valueObject is Ipc.Core.Work.ProgramBuffer raw && raw.Ccsid == _job.Ccsid
            ? raw.ToArray() : encoding.GetBytes(ClExpression.Text(valueObject));
        if (value.Length > length) throw new CpfException("IPC0126", "Value exceeds the selected byte range.");
        var bytes = Enumerable.Repeat(encoding.GetBytes(" ")[0], length).ToArray(); value.CopyTo(bytes, 0);
        store.Write(_job.Key, area, start - 1, bytes); return CommandResult.Ok("Job data area changed.");
    }
    private Ipc.Core.Work.ProgramArgument ClLocalDataArea()
    {
        if (_job is null) throw new CpfException("CPF1241", "The local data area requires an executing job.");
        var store = new JobDataAreaStore(_system.Connections);
        Ipc.Core.Work.ProgramBuffer Read() => new(store.Read(_job.Key, JobDataArea.Local), _job.Ccsid);
        object Normalize(object? value) => value is Ipc.Core.Work.ProgramBuffer raw && raw.Length == 1024 && raw.Ccsid == _job.Ccsid
            ? raw : throw new CpfException("IPC0126", "Invalid local-data-area byte buffer.");
        return new(() => Read(), value => store.Write(_job.Key, JobDataArea.Local, 0, ((Ipc.Core.Work.ProgramBuffer)value!).ToArray()),
            Normalize, "*CHAR", 1024, buffer: Read);
    }

    private RpgFileHandle OpenRpgFile(string alias)
    {
        var environment = EnvironmentForJob(); var definition = environment.Resolve(alias);
        var name = definition?.Name ?? alias;
        var library = definition?.Library ?? FindFileLibrary(name) ?? throw new CpfException("CPF9801", "RPG file not found.");
        var member = definition?.Member ?? _files.FirstMember(library, name);
        var path = environment.OpenPath(library + "/" + name + "/" + member, definition?.Share ?? false,
            definition?.Scope ?? JobEnvironmentScope.Call, () =>
            {
                var allocation = new JobLockStore(_system.Connections).AcquireWithLifetime(_job!.Key,
                    new(library, name, ObjectType.File), JobLockMode.SharedRead, TimeSpan.Zero, _cancellationToken, "OpenPath", Guid.NewGuid().ToString("N"));
                try { return new RpgJobPath(new RpgFileCursor(new RpgSqliteFileAccess(_files), library, name, member), allocation); }
                catch { allocation.Dispose(); throw; }
            });
        try
        {
            // Refresh data/authority and command record locks while preserving the shared position.
            path.Value.Cursor.Reload();
            return new RpgFileHandle(() => path.Value.Cursor, path.Dispose, path.Close);
        }
        catch { path.Dispose(); throw; }
    }
    private sealed class RpgJobPath(RpgFileCursor cursor, IDisposable allocation) : IDisposable
    {
        public RpgFileCursor Cursor { get; } = cursor;
        public void Dispose() { try { Cursor.Dispose(); } finally { allocation.Dispose(); } }
    }
}
