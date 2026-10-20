using Ipc.Db.Definitions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly HashSet<(RpgDataStructure, RpgDsElement, int)> _nullIndicators = new();
    private HashSet<(RpgDataStructure, RpgDsElement, int)> _staticNullIndicators = new();
    private HashSet<(RpgDataStructure, RpgDsElement, int)> NullIndicators(NativeView view)
        => ExportedStructure(view.Structure) ? _exportStorage.Nulls : _program.IsNativeProcedure && (view.Structure.StaticLocal || view.Structure.Elements.Any(e => _program.FindField(e.Name)?.StaticLocal == true))
            ? ModuleContext._staticNullIndicators : _nullIndicators;
    internal NativeView NullReference(RpgExpr expression)
    {
        if (_program.NullControl != "*USRCTL" || !TryNativeReference(expression, out var view) || view.IsArray || view.Element is not { NullCapable: true })
            throw NativeFailure(105, "%NULLIND requires ALWNULL(*USRCTL) and a null-capable scalar or indexed element.");
        return view;
    }
    internal bool ReadNullIndicator(NativeView view)
    {
        if (_program.GlobalStructures.Contains(view.Structure)) return GlobalContext!.ReadNullIndicator(view);
        EnsureArgumentStorage(view);
        if (BorrowedNullIndicator(view) is { } borrowed) return borrowed.Map.Read(borrowed.Index);
        if (view.Element is { NullCapable: true } && NamedNullIndicator(view) is { } named) return RpgValues.ToBool(ReadNative(named));
        return view.Element is { NullCapable: true } element && NullIndicators(view).Contains((view.Structure, element, view.Offset));
    }
    internal void WriteNullIndicator(NativeView view, bool value)
    {
        if (_program.GlobalStructures.Contains(view.Structure)) { GlobalContext!.WriteNullIndicator(view, value); return; }
        EnsureArgumentStorage(view); EnsureNativeWritable(view);
        if (view.Binding?.Reference.IsReadOnly == true || view.Binding?.Memory?.IsReadOnly == true) throw NativeFailure(105, "A read-only argument cannot change its null indicator.");
        if (view.Element is not { NullCapable: true } element) throw NativeFailure(105, "The field has no null indicator.");
        if (BorrowedNullIndicator(view) is { } borrowed) { borrowed.Map.Write(borrowed.Index, value); return; }
        if (NamedNullIndicator(view) is { } named) { WriteCapturedNative(named, value); return; }
        var key = (view.Structure, element, view.Offset);
        if (value) NullIndicators(view).Add(key); else NullIndicators(view).Remove(key);
    }
    private NativeView? ScalarFileNullReference(NativeView view) => view.Element is not { NullCapable: true } ? null :
        !view.IsArray ? view : view.Count == 1 ? NativeIndex(view, 1) : null;
    internal NativeView? FileNullReference(string name) => TryNativeReference(new RpgFieldRef(name), out var view) ? ScalarFileNullReference(view) : null;
    internal void SetFileNullIndicator(string name, bool value)
    {
        if (FileNullReference(name) is { } view) WriteNullIndicator(view, value);
        else if (TryNativeReference(new RpgFieldRef(name), out var data) && NamedNullIndicator(data) is { } named) WriteCapturedNative(named, false);
    }
    internal object? NullOutput(RpgDiskField field, object? value, NativeView? view)
        => field.NullCapable && _program.NullControl == "*USRCTL" && view is not null && ReadNullIndicator(view) ? null : value;

    internal IReadOnlyDictionary<string, object?> PrepareNullInput(IReadOnlyDictionary<string, object?> record, RecordFormat format)
    {
        if (!record.Values.Any(v => v is null)) return record;
        var values = new Dictionary<string, object?>(record, StringComparer.OrdinalIgnoreCase);
        foreach (var field in format.Fields)
        {
            if (!record.TryGetValue(field.Name, out var value) || value is not null) continue;
            if (!field.NullCapable || _program.NullControl == "*NO") throw new RpgRuntimeException("Null input requires ALWNULL(*INPUTONLY) or ALWNULL(*USRCTL).");
            values[field.Name] = _program.NullControl == "*INPUTONLY" && field.DefaultValue is { Value: not null } supplied ? supplied.Value :
                _program.NullControl == "*INPUTONLY" && field.CurrentDatetimeDefault ? field.Type switch {
                    FieldType.Date => DateOnly.FromDateTime(CurrentTime.DateTime), FieldType.Time => CurrentTime.TimeOfDay, _ => CurrentTime.DateTime } : SystemDefault(field.Type);
        }
        return values;
    }
    private static object SystemDefault(FieldType type) => type switch {
        FieldType.Alpha => "", FieldType.Logic => false, FieldType.Binary => 0L, FieldType.Float => 0d,
        FieldType.Date => new DateOnly(1900, 1, 1), FieldType.Time => TimeSpan.Zero,
        FieldType.Timestamp => new DateTime(1900, 1, 1), _ => 0m };
}
