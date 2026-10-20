using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private static RpgDsElement? WorkstationRecordElement(RpgDataStructure structure, RpgDisplayField field)
    {
        if (structure.RecordOrigin?.External is null) return structure.Find(field.Name);
        var member = structure.Elements.SingleOrDefault(e => e.ExternalName == field.External);
        return member is null ? null : new() { Name = member.Name, Kind = RpgWorkstationRecords.Kind(field), Length = field.Length,
            Decimals = field.Decimals, Offset = member.Offset, StorageLength = field.Length, Stride = field.Length, OpaqueBytes = field.Opaque };
    }
    internal NativeView CaptureWorkstationStructure(string text, RpgWorkstationBinding file, RpgDisplayRecord record, RpgOpcode opcode, bool input)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(text), out var view) || view.IsArray || view.Element is not null ||
            view.Layout.RecordOrigin is not { } origin || !RpgCompiler.WorkstationRecordMatches(origin, file, record, opcode) ||
            view.Width != RpgWorkstationRecords.Length(record, origin.Extract))
            throw NativeFailure(105, "The WORKSTN result structure does not match the record operation.");
        EnsureArgumentStorage(view);
        if (input)
        {
            EnsureNativeWritable(view);
            foreach (var field in record.Fields.Where(f => RpgWorkstationRecords.Input(record, f)))
            {
                var member = view.Layout.RecordOrigin?.External is null ? view.Layout.Find(field.Name)! : view.Layout.Elements.Single(e => e.ExternalName == field.External);
                PrepareNullIndicatorWrite(NativeMember(view, member.LocalName ?? member.Name));
            }
            if (view.Binding?.Reference.IsReadOnly == true || view.Binding?.Memory?.IsReadOnly == true)
                throw new CpfException("CPF3C90", "A read-only parameter cannot receive WORKSTN input.");
        }
        return view;
    }
    internal IReadOnlyDictionary<string, object?> SendWorkstationRecord(NativeView view, RpgDisplayRecord record)
    {
        var image = NativeBuffer(view).ToArray(); var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in record.Fields.Where(f => RpgWorkstationRecords.Output(record, f)))
        {
            var element = WorkstationRecordElement(view.Layout, field) ?? throw NativeFailure(105, "Missing WORKSTN output field.");
            result.Add(field.Name, ReadNative(new(view.Structure, image, element.Offset, element.StorageLength, element, false, 1)));
        }
        return result;
    }
    internal Action PrepareWorkstationRecord(NativeView view, RpgDisplayRecord record, IReadOnlyDictionary<string, object?> values)
    {
        if (record.Fields.Where(f => RpgWorkstationRecords.Input(record, f)).Any(f => !values.ContainsKey(f.Name)))
            throw NativeFailure(105, "WORKSTN input omitted a result field.");
        var image = NativeBuffer(view).ToArray();
        foreach (var (name, input) in values)
        {
            var field = record.Fields.SingleOrDefault(f => f.Name == name && RpgWorkstationRecords.Input(record, f));
            if (field is null || WorkstationRecordElement(view.Layout, field) is not { } element)
                throw NativeFailure(105, "Unexpected WORKSTN input field: " + name);
            object? value = input;
            if (element.Kind == RpgFieldKind.Character && !element.OpaqueBytes)
            {
                try
                {
                    var encoded = NativeEncoding.GetBytes(RpgValues.ToText(value));
                    if (encoded.Length > element.StorageLength) throw NativeFailure(452, "WORKSTN input exceeds the result field's byte width.");
                    var padded = Enumerable.Repeat(NativeBlank, element.StorageLength).ToArray(); encoded.CopyTo(padded, 0); value = new ProgramBuffer(padded, NativeCcsid);
                }
                catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
            }
            var part = new NativeView(view.Structure, image, element.Offset, element.StorageLength, element, false, 1);
            NormalizeNative(part, value, strict: true).CopyTo(image, element.Offset);
        }
        var prepared = NormalizeNative(view, new ProgramBuffer(image, NativeCcsid), strict: true);
        return () =>
        {
            WriteNativeBytes(view, prepared);
            foreach (var name in values.Keys)
            {
                var field = record.Fields.Single(f => f.Name == name);
                var member = view.Layout.RecordOrigin?.External is null ? view.Layout.Find(name)! : view.Layout.Elements.Single(e => e.ExternalName == field.External);
                var actual = NativeMember(view, member.LocalName ?? member.Name);
                if (ScalarFileNullReference(actual) is { } nullable) WriteNullIndicator(nullable, false);
                else if (NamedNullIndicator(actual) is { } named) WriteCapturedNative(named, false);
            }
        };
    }
}
