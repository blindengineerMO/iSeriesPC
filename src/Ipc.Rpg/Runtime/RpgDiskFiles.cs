using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

/// <summary>Shared compile/open format description, independent of physical file identity.</summary>
public static class RpgDiskLayouts
{
    public static TimeSpan RecordWait(FileDefinition definition, int? overriding = null)
    {
        if (definition.RecordLockWaitSeconds is < 0 or > 32767 || overriding is < -1 or > 32767)
            throw new CpfException("CPF4131", "Invalid database record-wait attribute.");
        var seconds = overriding ?? definition.RecordLockWaitSeconds;
        return seconds == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
    }
    public static RpgDiskLayout Describe(RpgDiskRequest request, string library, string name, FileDefinition definition)
    {
        if (definition.Formats.Count != 1 || definition.Attribute is not (FileAttribute.Physical or FileAttribute.Logical or FileAttribute.Source))
            throw new CpfException("CPF4131", "DISK requires one database record format.");
        var format = definition.PrimaryFormat;
        if (request.Renames.Any(r => r.External != format.Name)) throw new CpfException("CPF4131", "RENAME references an unavailable database record.");
        var fields = format.Fields.Select(field =>
        {
            if (field.NullCapable || field.VariableLength || field.Type == FieldType.Alpha && field.Ccsid == 65535)
                throw new CpfException("CPF4131", "DISK declarations do not yet support null-capable, varying or opaque database fields.");
            var kind = field.Type switch { FieldType.Alpha => RpgFieldKind.Character, FieldType.Zoned => RpgFieldKind.Zoned,
                FieldType.Packed => RpgFieldKind.Packed, FieldType.Binary => RpgFieldKind.Integer, FieldType.Float => RpgFieldKind.Float,
                FieldType.Logic => RpgFieldKind.Indicator, FieldType.Date => RpgFieldKind.Date, FieldType.Time => RpgFieldKind.Time,
                FieldType.Timestamp => RpgFieldKind.Timestamp, _ => throw new CpfException("CPF4131", "Unsupported database field type.") };
            var length = field.Type == FieldType.Binary ? field.Length switch { 1 => 3, 2 => 5, 4 => 10, 8 => 20, _ => 0 } : field.Length;
            return new RpgDiskField(field.Name, request.Prefix + field.Name, kind, length, field.Decimals, field.Ccsid,
                field.Sequence, field.Descending, field.Position, field.StorageLength);
        }).ToArray();
        var record = RpgDiskBindings.RecordName(request, request.Renames.SingleOrDefault()?.Name ?? format.Name);
        var result = new RpgDiskLayout(library + "/" + name,
            RpgDiskLayout.SignatureFor(format.Name, record, format.RecordLength, format.BufferLayoutVersion, fields), format.Name, record,
            format.RecordLength, format.BufferLayoutVersion, fields);
        RpgDiskBindings.Validate(new(request, result)); return result;
    }
    public static void Validate(RpgDiskBinding binding, RpgFileCursor cursor)
    {
        RpgDiskBindings.Validate(binding);
        if (cursor.IsKeyed != binding.Request.Keyed || cursor.IsUpdateRead != binding.Request.Update || cursor.DeleteAccess != binding.Request.Delete ||
            cursor.InputAccess != binding.Request.Input || cursor.OutputAccess != binding.Request.Output ||
            Describe(binding.Request, cursor.Library, cursor.Name, cursor.Definition).Signature != binding.Layout.Signature)
            throw new CpfException("CPF4131", "Database record format changed; recompile the RPG program.");
        cursor.Reload();
    }
}

internal sealed class RpgDiskState : IDisposable
{
    internal bool Initialized { get; set; }
    internal Dictionary<string, RpgFileHandle> Files { get; } = new(StringComparer.Ordinal);
    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var handle in Files.Values)
            try { handle.Close(); } catch (Exception error) { (errors ??= []).Add(error); }
        Files.Clear(); Initialized = false;
        if (errors is not null) throw new AggregateException("Could not close RPG disk files.", errors);
    }
}

public sealed partial class RpgInterpreter
{
    private RpgRuntimeContext? _context;
    private void InitializeExternalFiles(RpgRuntimeContext context)
    {
        if (context.Program.DiskFiles.Count == 0 || context.DiskState.Initialized || context.WorkstationState.Initialized)
        { InitializeWorkstations(context); InitializeDisks(context); return; }
        try
        {
            foreach (var name in context.Program.FileSpecs.AsEnumerable().Reverse())
            {
                var disk = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name);
                var workstation = context.Program.Workstations.SingleOrDefault(f => f.Request.Name == name);
                if (disk is { Request.UserOpen: false, Request.IsTemplate: false }) context.DiskState.Files.Add(name, ImplicitFile(name, () => CreateDisk(disk)));
                else if (workstation is { Request.UserOpen: false }) context.WorkstationState.Files.Add(name, ImplicitFile(name, () => CreateWorkstation(workstation)));
                else continue;
                context.SetFileStatus(name, clearFileEof: true);
            }
            context.DiskState.Initialized = true; context.WorkstationState.Initialized = true;
        }
        catch { try { context.CloseDiskFiles(); } finally { context.CloseWorkstations(); } throw; }
    }
    private void InitializeDisks(RpgRuntimeContext context)
    {
        var state = context.DiskState;
        if (state.Initialized)
        {
            foreach (var file in context.Program.DiskFiles.Where(f => state.Files.ContainsKey(f.Request.Name)))
            {
                var handle = state.Files[file.Request.Name]; handle.Rebind(_host.CancellationToken);
                RpgDiskLayouts.Validate(file, handle.Cursor);
            }
            return;
        }
        try
        {
            foreach (var file in context.Program.DiskFiles.Where(f => !f.Request.UserOpen && !f.Request.IsTemplate).Reverse())
            {
                state.Files.Add(file.Request.Name, ImplicitFile(file.Request.Name, () => CreateDisk(file))); context.SetFileStatus(file.Request.Name, clearFileEof: true);
            }
            state.Initialized = true;
        }
        catch { context.CloseDiskFiles(); throw; }
    }
    private RpgFileHandle CreateDisk(RpgDiskBinding binding)
    {
        if (binding.Request.IsTemplate) throw FileFailure(105, "A TEMPLATE file has no runtime storage.", binding.Request.Name);
        RpgFileHandle handle;
        if (_host.OpenDisk is { } open) handle = open(binding);
        else
        {
            var runtime = binding.Request.RuntimeFile == "*EXTDESC" ? binding.Request.DescriptionFile : binding.Request.RuntimeFile;
            var parts = runtime.Split('/'); var name = parts[^1];
            var library = parts.Length == 2 && parts[0] is not ("*LIBL" or "*CURLIB") ? parts[0] :
                _host.LibraryResolver?.Invoke(parts.Length == 2 ? parts[0] : "*LIBL", name) ?? throw new RpgRuntimeException("DISK file is not available: " + runtime);
            var access = _host.Files ?? throw new RpgRuntimeException("No DISK file access is configured.");
            var wait = binding.Request.Update ? RecordWaitForOpen(access, library, name) : (TimeSpan?)null;
            var cursor = new RpgFileCursor(access, library, name,
                name, _host.CancellationToken, binding.Request.Keyed, binding.Request.Update, binding.Request.Delete, binding.Request.Output, binding.Request.Input, wait);
            handle = new(() => cursor, cursor.Dispose, rebind: cursor.RebindCancellation);
        }
        try { RpgDiskLayouts.Validate(binding, handle.Cursor); return handle; }
        catch { handle.Close(); throw; }
    }
    private static TimeSpan RecordWaitForOpen(IRpgFileAccess access, string library, string name) => RpgDiskLayouts.RecordWait(
        access.GetDefinition(library, name) ?? throw new RpgRuntimeException("DISK file is not available: " + library + "/" + name));
    private bool OpenDisk(RpgStatement statement, RpgRuntimeContext context)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name);
        if (binding is null) return false;
        if (context.DiskState.Files.ContainsKey(name)) throw FileFailure(1215, "DISK file is already open: " + name, name);
        context.DiskState.Files.Add(name, FileIo(name, () => CreateDisk(binding), 1217)); context.SetFileStatus(name, clearFileEof: true); return true;
    }
    private static bool CloseDisk(RpgStatement statement, RpgRuntimeContext context)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name);
        if (binding is null) return false;
        if (binding.Request.IsTemplate) throw FileFailure(105, "A TEMPLATE file has no runtime I/O.", name);
        if (!context.DiskState.Files.Remove(name, out var handle)) throw FileFailure(1211, "DISK file is not open: " + name, name);
        handle.Close(); return true;
    }
    private RpgFileCursor? DeclaredDisk(string name)
    {
        var context = _context ?? throw new InvalidOperationException("No RPG execution context.");
        var binding = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name || f.Layout.Record == name);
        if (binding is null) return null;
        if (binding.Request.IsTemplate) throw FileFailure(105, "A TEMPLATE file has no runtime I/O.", binding.Request.Name);
        if (!context.DiskState.Files.TryGetValue(binding.Request.Name, out var handle)) throw FileFailure(1211, "DISK file is not open: " + binding.Request.Name, binding.Request.Name);
        var cursor = handle.Cursor; RpgDiskLayouts.Validate(binding, cursor); return cursor;
    }
    private void CopyDiskRecord(RpgRuntimeContext context, RpgStatement statement, IReadOnlyDictionary<string, object?> record, RpgFileCursor cursor, RpgRuntimeContext.NativeView? receiver = null)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name || f.Layout.Record == name);
        if (binding is null) { CopyRecordToContext(context, record); return; }
        try
        {
            if (receiver is not null)
            {
                FileIo(binding.Request.Name, () => context.ReceiveRecord(receiver, binding, record)); return;
            }
            var values = FileIo(binding.Request.Name, () => binding.Layout.Fields.Select(field =>
                (field.Name, Value: context.NormalizeFileValue(field.Name, record[field.External]))).ToArray());
            foreach (var field in values) context.WriteValue(field.Name, field.Value);
        }
        catch { cursor.InvalidateCurrent(); throw; }
    }
    private static RpgDiskBinding? DiskBinding(RpgRuntimeContext context, RpgStatement statement)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        return context.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name || f.Layout.Record == name);
    }
    private int UnlockOpcode(RpgStatement statement, RpgRuntimeContext context)
    {
        var binding = DiskBinding(context, statement);
        if (binding is null || !binding.Request.Update || FirstFile(statement).ToUpperInvariant() != binding.Request.Name)
            throw new RpgRuntimeException("UNLOCK requires a declared update DISK file name.");
        GetCursor(binding.Request.Name).Unlock(); return -1;
    }
    private string StatusFile(RpgStatement statement)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        return _context?.Program.DiskFiles.SingleOrDefault(f => f.Request.Name == name || f.Layout.Record == name)?.Request.Name ??
            _context?.Program.Workstations.SingleOrDefault(f => f.Request.Name == name || f.Layout.Records.Any(r => r.Name == name))?.Request.Name ?? name;
    }
}
