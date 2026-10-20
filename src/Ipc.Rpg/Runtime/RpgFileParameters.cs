using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

internal sealed class RpgFileReference
{
    private readonly RpgRuntimeContext owner;
    private readonly RpgDiskBinding? disk;
    private readonly RpgWorkstationBinding? workstation;
    private readonly Action? check;
    internal RpgFileReference(RpgRuntimeContext owner, RpgDiskBinding binding, Action? check = null)
    { this.owner = owner; disk = binding; this.check = check; }
    internal RpgFileReference(RpgRuntimeContext owner, RpgWorkstationBinding binding, Action? check = null)
    { this.owner = owner; workstation = binding; this.check = check; }
    internal RpgRuntimeContext Owner { get { check?.Invoke(); return owner; } }
    internal RpgDiskBinding Binding { get { check?.Invoke(); return disk ?? throw new RpgRuntimeException("The reference is not a DISK file.") { StatusCode = 105 }; } }
    internal RpgWorkstationBinding Workstation { get { check?.Invoke(); return workstation ?? throw new RpgRuntimeException("The reference is not a WORKSTN file.") { StatusCode = 105 }; } }
    internal string Name { get { check?.Invoke(); return disk?.Request.Name ?? workstation!.Request.Name; } }
    internal bool IsWorkstation { get { check?.Invoke(); return workstation is not null; } }
    internal RpgFileReference Scoped(Action next) => disk is { } file ? new(owner, file, () => { check?.Invoke(); next(); })
        : new(owner, workstation!, () => { check?.Invoke(); next(); });
}

public sealed partial class RpgRuntimeContext
{
    internal Func<RpgRuntimeException, bool>? FileErrorHandler { get; set; }
    internal Func<RpgDiskBinding, RpgFileHandle>? FileOpener { get; set; }
    internal Func<RpgWorkstationBinding, IRpgWorkstation>? WorkstationOpener { get; set; }
    private readonly Dictionary<string, RpgFileReference> _fileReferences = new(StringComparer.OrdinalIgnoreCase);
    public static bool ContainsFileArguments(IEnumerable<object?> arguments) => arguments.Any(a => a is RpgFileReference or ProgramArgument { Type: "*FILE" });

    internal RpgFileReference? FileReference(string name)
    {
        if (_program.DiskFiles.Any(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Request.Procedure == _program.NativeProcedureName && !f.Request.IsParameter && f.Request.Procedure is not null)) return null;
        if (_program.Workstations.Any(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Request.Procedure == _program.NativeProcedureName && !f.Request.IsParameter && f.Request.Procedure is not null)) return null;
        if (_program.FindField(name) is not { IsFileParameter: true } field)
        {
            // File operands retain their global binding when local data shadows the name.
            if (_program.GlobalFields.Any(f => f.IsFileParameter && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return GlobalContext!.FileReference(name);
            return null;
        }
        if (_program.GlobalFields.Contains(field)) return GlobalContext!.FileReference(name);
        if (_unpassedParameters.Contains(name) || _omittedParameters.Contains(name) || !_fileReferences.TryGetValue(name, out var reference))
            throw NativeFailure(202, "The file parameter was not passed or was omitted.");
        _ = reference.Name; return reference;
    }
    internal RpgFileReference CaptureFileArgument(RpgExpr expression, RpgDiskBinding formal)
    {
        var actual = expression is RpgFieldRef name ? _program.DiskFiles.SingleOrDefault(f => f.Request.Name.Equals(name.Name, StringComparison.OrdinalIgnoreCase)) : null;
        if (actual is null || actual.Request.IsTemplate || RpgDiskBindings.FamilyKey(actual) != RpgDiskBindings.FamilyKey(formal))
            throw NativeFailure(105, "A LIKEFILE actual must be a runtime file from the same parent family.");
        return FileReference(actual.Request.Name) ?? new(actual.Request.Procedure is null ? ModuleContext : this, actual);
    }
    internal ProgramArgument BorrowFile(RpgFileReference reference, Action check)
    {
        var scoped = reference.Scoped(check);
        object? Read() { check(); _ = scoped.Name; return scoped; }
        return new(Read, _ => throw NativeFailure(105, "A file reference cannot be replaced."), value =>
        { check(); if (!ReferenceEquals(value, scoped)) throw NativeFailure(105, "A file reference cannot be replaced."); return value; }, "*FILE") { IsReadOnly = true };
    }
    internal void BindFileParameter(RpgField field, object? argument)
    {
        var value = argument is ProgramArgument { Type: "*FILE" } cell ? cell.Value : argument;
        if (value is not RpgFileReference reference || (field.WorkstationParameter is { } display
                ? !reference.IsWorkstation || !WorkstationCompatible(display, reference.Workstation)
                : field.FileParameter is not { } formal || reference.IsWorkstation || !FileCompatible(formal, reference.Binding)))
            throw NativeFailure(105, "An RPG file parameter requires a compatible borrowed file reference.");
        _fileReferences[field.Name] = reference;
    }
    internal static bool FileCompatible(RpgDiskBinding formal, RpgDiskBinding actual) =>
        formal.Request.Keyed == actual.Request.Keyed && formal.Request.Input == actual.Request.Input && formal.Request.Output == actual.Request.Output &&
        formal.Request.Update == actual.Request.Update && formal.Request.Delete == actual.Request.Delete && formal.Request.Prefix == actual.Request.Prefix &&
        formal.Request.Renames.SequenceEqual(actual.Request.Renames) && formal.Layout.ExternalRecord == actual.Layout.ExternalRecord &&
        formal.Layout.RecordLength == actual.Layout.RecordLength && formal.Layout.BufferLayoutVersion == actual.Layout.BufferLayoutVersion && formal.Layout.Fields.SequenceEqual(actual.Layout.Fields);
    internal bool InvokeFileParameterError(RpgRuntimeException error)
    {
        if (error.StatusFile is null || FileReference(error.StatusFile) is not { } reference) return false;
        var target = reference.Owner;
        var translated = new RpgRuntimeException(error.Message) { StatusCode = error.StatusCode, FileError = true,
            StatusFile = reference.Name, ServiceError = error.ServiceError,
            ErrorHandlerInvocations = error.ErrorHandlerInvocations, SuppressErrorSubroutines = error.SuppressErrorSubroutines };
        return target.FileErrorHandler?.Invoke(translated) == true;
    }
    private FileStatus FileParameterStatus(string name)
    {
        var reference = FileReference(name);
        return reference is null ? StatusFor(name).GetValueOrDefault(name, new(false, false, false)) : reference.Owner.FileParameterStatus(reference.Name);
    }
    private int FileParameterNumericStatus(string name)
    {
        var reference = FileReference(name);
        return reference is null ? NumericStatusFor(name).GetValueOrDefault(name) : reference.Owner.FileParameterNumericStatus(reference.Name);
    }
    private bool FileParameterOpen(string name)
    {
        var reference = FileReference(name);
        return reference is not null ? reference.Owner.FileParameterOpen(reference.Name) : _program.Workstations.Any(f => f.Request.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ? WorkstationStateFor(name).Files.ContainsKey(name.ToUpperInvariant()) : DiskStateFor(name).Files.ContainsKey(name.ToUpperInvariant());
    }
}
