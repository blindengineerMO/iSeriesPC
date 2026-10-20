using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using System.Text;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private static RpgDsElement DiskRecordElement(RpgDataStructure structure, RpgDiskField field)
    {
        if (structure.RecordOrigin?.External is null) return structure.Find(field.Name)!;
        var member = structure.Elements.Single(e => e.ExternalName == field.External);
        return new() { Name = member.Name, Kind = field.Kind, Length = field.Length, Decimals = field.Decimals,
            Offset = member.Offset, StorageLength = field.StorageLength, Stride = field.StorageLength };
    }
    internal NativeView CaptureRecordStructure(string text, RpgDiskBinding file, RpgOpcode opcode, bool input)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(text), out var view) || view.IsArray || view.Element is not null || view.Width != file.Layout.RecordLength ||
            view.Layout.RecordOrigin is not { } origin || !RpgCompiler.RecordMatches(origin, file, opcode))
            throw NativeFailure(105, "The result data structure does not match the record operation.");
        EnsureArgumentStorage(view);
        if (input)
        {
            EnsureNativeWritable(view);
            foreach (var field in file.Layout.Fields) PrepareNullIndicatorWrite(RecordValueView(view, field));
            if (view.Binding?.Reference.IsReadOnly == true || view.Binding?.Memory?.IsReadOnly == true)
                throw new CpfException("CPF3C90", "A read-only parameter cannot receive record input.");
        }
        return view;
    }
    internal void ReceiveRecord(NativeView view, RpgDiskBinding file, IReadOnlyDictionary<string, object?> record, IReadOnlyDictionary<string, object?> original)
    {
        var image = Enumerable.Repeat(NativeBlank, view.Width).ToArray();
        try
        {
            foreach (var field in file.Layout.Fields)
            {
                var element = DiskRecordElement(view.Layout, field);
                var part = new NativeView(view.Structure, image, element.Offset, element.StorageLength, element, false, 1);
                var value = record[field.External];
                if (element.Kind == RpgFieldKind.Character)
                {
                    try
                    {
                        var text = NativeEncoding.GetBytes(RpgValues.ToText(value));
                        if (text.Length > element.StorageLength) throw NativeFailure(452, "Record text exceeds the native result field's byte width.");
                        var padded = Enumerable.Repeat(NativeBlank, element.StorageLength).ToArray(); text.CopyTo(padded, 0);
                        value = new ProgramBuffer(padded, NativeCcsid);
                    }
                    catch (EncoderFallbackException error) { throw NativeFailure(452, error.Message); }
                }
                NormalizeNative(part, value, strict: true).CopyTo(image, element.Offset);
            }
        }
        catch (RpgRuntimeException error)
        { throw new RpgRuntimeException(error.Message) { StatusCode = error.StatusCode ?? 1299, FileError = true, StatusFile = file.Request.Name, ServiceError = error }; }
        WriteCapturedNative(view, new ProgramBuffer(image, NativeCcsid));
        foreach (var field in file.Layout.Fields)
        {
            var member = view.Layout.RecordOrigin?.External is null ? view.Layout.Find(field.Name)! : view.Layout.Elements.Single(e => e.ExternalName == field.External);
            var actual = NativeMember(view, member.LocalName ?? member.Name);
            if (ScalarFileNullReference(actual) is { } nullable) WriteNullIndicator(nullable, original[field.External] is null);
            else if (NamedNullIndicator(actual) is { } named) WriteCapturedNative(named, false);
        }
    }
    private NativeView RecordValueView(NativeView view, RpgDiskField field)
    {
        var member = view.Layout.RecordOrigin?.External is null ? view.Layout.Find(field.Name)! : view.Layout.Elements.Single(e => e.ExternalName == field.External);
        return NativeMember(view, member.LocalName ?? member.Name);
    }
    private NativeView? RecordNullReference(NativeView view, RpgDiskField field) => ScalarFileNullReference(RecordValueView(view, field));
    internal IReadOnlyDictionary<string, object?> SendRecord(NativeView view, RpgDiskBinding file)
    {
        var image = NativeBuffer(view).ToArray(); var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in file.Layout.Fields)
        {
            var element = DiskRecordElement(view.Layout, field);
            var value = ReadNative(new(view.Structure, image, element.Offset, element.StorageLength, element, false, 1));
            result.Add(field.External, NullOutput(field, value, RecordNullReference(view, field)));
        }
        return result;
    }
}

public sealed partial class RpgInterpreter
{
    private static RpgRuntimeContext.NativeView? RecordReceiver(RpgStatement statement, RpgRuntimeContext context)
    {
        var file = DiskBinding(context, statement);
        if (file?.Request.IsTemplate == true || file?.Request.Qualified == true && statement.RecordDataStructure is null)
            throw new RpgRuntimeException("TEMPLATE files have no runtime I/O; qualified files require result structures.") { StatusCode = 105 };
        return statement.RecordDataStructure is { } text ? context.CaptureRecordStructure(text,
            file ?? throw new RpgRuntimeException("A result data structure requires a declared DISK file."), statement.Opcode, true) : null;
    }

    private static IReadOnlyDictionary<string, object?> RecordOutput(RpgStatement statement, RpgRuntimeContext context, RpgDiskBinding file)
        => file.Request.IsTemplate || file.Request.Qualified && statement.RecordDataStructure is null
            ? throw new RpgRuntimeException("TEMPLATE files have no runtime I/O; qualified files require result structures.") { StatusCode = 105 }
            : statement.RecordDataStructure is { } text ? context.SendRecord(context.CaptureRecordStructure(text, file, statement.Opcode, false), file) :
            file.Layout.Fields.ToDictionary(field => field.External, field => context.NullOutput(field, context.ReadFileValue(field.Name), context.FileNullReference(field.Name)), StringComparer.OrdinalIgnoreCase);
}
