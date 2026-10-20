using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private readonly Dictionary<RpgDataStructure, (NativeView Root, ProgramArgumentNullMap Map)> _borrowedNullMaps = new();
    internal void RequireNullParameterType(RpgField formal, RpgExpr expression, NativeView view)
    {
        if (RpgCompiler.NullParameterTypeError(_program, expression, formal) is { } error) throw NativeFailure(105, error);
        if (view.IsArray && view.Stride != view.UnitWidth) throw NativeFailure(105, "A noncontiguous array cannot pass its native data and null map as one argument.");
    }
    private static int NullLeafCount(NativeView view) => checked((view.IsArray ? view.Count : 1) * (view.Element is null ? NullLayoutCount(view.Layout) : 1));
    private static int NullLayoutCount(RpgDataStructure layout) => layout.Elements.Sum(e => checked(e.Dimension * (e.NestedStructure is { } nested ? NullLayoutCount(nested) : 1)));
    private NativeView NullLeafAt(NativeView view, int index)
    {
        if (view.IsArray)
        {
            var unit = view.Element is null ? NullLayoutCount(view.Layout) : 1;
            view = NativeIndex(view, index / unit + 1); index %= unit;
        }
        if (view.Element is not null) return index == 0 ? view : throw NativeFailure(105, "Null-map index exceeds its native layout.");
        foreach (var element in view.Layout.Elements)
        {
            var count = checked(element.Dimension * (element.NestedStructure is { } nested ? NullLayoutCount(nested) : 1));
            if (index < count) return NullLeafAt(NativeMember(view, element), index);
            index -= count;
        }
        throw NativeFailure(105, "Null-map index exceeds its native layout.");
    }
    private int? NullLeafOrdinal(NativeView root, NativeView leaf)
    {
        var ordinal = 0;
        if (root.IsArray)
        {
            var selected = (leaf.Offset - root.Offset) / root.Stride;
            if (selected < 0 || selected >= root.Count) return null;
            ordinal = checked(selected * (root.Element is null ? NullLayoutCount(root.Layout) : 1)); root = NativeIndex(root, selected + 1);
        }
        if (root.Element is not null) return ReferenceEquals(root.Element, leaf.Element) && root.Offset == leaf.Offset ? ordinal : null;
        foreach (var element in root.Layout.Elements)
        {
            var child = NativeMember(root, element);
            if (leaf.Offset >= child.Offset && leaf.Offset < (long)child.Offset + (child.Count - 1) * child.Stride + child.UnitWidth && NullLeafOrdinal(child, leaf) is { } found) return ordinal + found;
            ordinal = checked(ordinal + NullLeafCount(child));
        }
        return null;
    }
    private readonly Dictionary<(object, RpgDataStructure, RpgDsElement), object> _nullStorageOwners = new();
    private ProgramArgumentStorage? NullStorage(NativeView leaf)
    {
        if (_program.GlobalStructures.Contains(leaf.Structure)) return GlobalContext!.NullStorage(leaf);
        if (BorrowedNullIndicator(leaf) is { } borrowed) return borrowed.Map.Storage(borrowed.Index);
        if (NamedNullIndicator(leaf) is { } named) return NativeStorageRange(named);
        var key = ((object)NullIndicators(leaf), leaf.Structure, leaf.Element!);
        var owners = ExportedStructure(leaf.Structure) ? _exportStorage.NullOwners : ReferenceEquals(NullIndicators(leaf), ModuleContext._staticNullIndicators) ? ModuleContext._nullStorageOwners : _nullStorageOwners;
        if (!owners.TryGetValue(key, out var owner)) owners.Add(key, owner = new object());
        return new(owner, leaf.Offset, 1);
    }
    internal ProgramArgumentNullMap CaptureNullArgument(NativeView root, RpgField formal)
    {
        EnsureArgumentStorage(root);
        var count = NullLeafCount(root); var temporary = new HashSet<int>();
        bool Capable(int index) => !formal.IsDataStructure || NullLeafAt(root, index).Element!.NullCapable;
        bool Read(int index)
        {
            var leaf = NullLeafAt(root, index);
            return leaf.Element!.NullCapable ? ReadNullIndicator(leaf) : temporary.Contains(index);
        }
        void Validate(int index, bool value)
        {
            var leaf = NullLeafAt(root, index); EnsureArgumentStorage(leaf); EnsureNativeWritable(leaf);
            if (leaf.Element!.NullCapable) PrepareNullIndicatorWrite(leaf);
        }
        void Write(int index, bool value)
        {
            var leaf = NullLeafAt(root, index);
            if (leaf.Element!.NullCapable) WriteNullIndicator(leaf, value);
            else if (value) temporary.Add(index); else temporary.Remove(index);
        }
        return new(count, Capable, Read, Validate, Write, root.Binding?.Reference.IsReadOnly == true || root.Binding?.Memory?.IsReadOnly == true || root.Structure.ReadOnlyParameter || root.Element?.ReadOnlyParameter == true, index => { var leaf = NullLeafAt(root, index); return leaf.Element!.NullCapable ? NullStorage(leaf) : null; });
    }
    private void BindNullParameter(RpgField field, ProgramArgument argument)
    {
        if (argument.NullMap is not { } map || !TryNativeReference(new RpgFieldRef(field.Name), out var root) || map.Count != NullLeafCount(root))
            throw NativeFailure(105, "OPTIONS(*NULLIND) requires a compatible borrowed null map at entry.");
        for (var index = 0; index < map.Count; index++)
        {
            _host.CancellationToken.ThrowIfCancellationRequested();
            if (map.IsNullable(index) != NullLeafAt(root, index).Element!.NullCapable) throw NativeFailure(105, "Null-map capability does not match the procedure interface.");
        }
        _borrowedNullMaps.Add(root.Structure, (root, map.Scoped(() => { }, field.ReadOnlyParameter)));
    }
    private (ProgramArgumentNullMap Map, int Index)? BorrowedNullIndicator(NativeView view)
        => (_importedNullMaps.TryGetValue(view.Structure, out var binding) || _borrowedNullMaps.TryGetValue(view.Structure, out binding)) && NullLeafOrdinal(binding.Root, view) is { } index ? (binding.Map, index) : null;
    private void PrepareNullIndicatorWrite(NativeView view)
    {
        EnsureArgumentStorage(view); EnsureNativeWritable(view);
        if (view.Binding?.Reference.IsReadOnly == true || view.Binding?.Memory?.IsReadOnly == true) throw NativeFailure(105, "A read-only argument cannot change its null indicator.");
        if (BorrowedNullIndicator(view) is { Map.IsReadOnly: true }) throw NativeFailure(105, "A read-only borrowed null map cannot be changed.");
        if (NamedNullIndicator(view) is { } named)
        {
            EnsureArgumentStorage(named); EnsureNativeWritable(named);
            if (named.Binding?.Reference.IsReadOnly == true || named.Binding?.Memory?.IsReadOnly == true) throw NativeFailure(105, "A read-only named null indicator cannot be changed.");
        }
    }
}
