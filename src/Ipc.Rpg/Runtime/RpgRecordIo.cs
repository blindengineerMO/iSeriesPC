using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using System.Text;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal NativeView CaptureRecordStructure(string text, RpgDiskBinding file, RpgOpcode opcode, bool input)
    {
        if (!TryNativeReference(RpgExpressionParser.Parse(text), out var view) || view.IsArray || view.Element is not null || view.Width != file.Layout.RecordLength ||
            view.Structure.RecordOrigin is not { } origin || !RpgCompiler.RecordMatches(origin, file, opcode))
            throw NativeFailure(105, "The result data structure does not match the record operation.");
        EnsureArgumentStorage(view);
        if (input)
        {
            EnsureNativeWritable(view);
            if (view.Binding?.Reference.IsReadOnly == true || view.Binding?.Memory?.IsReadOnly == true)
                throw new CpfException("CPF3C90", "A read-only parameter cannot receive record input.");
        }
        return view;
    }
    internal void ReceiveRecord(NativeView view, RpgDiskBinding file, IReadOnlyDictionary<string, object?> record)
    {
        var image = Enumerable.Repeat(NativeBlank, view.Width).ToArray();
        try
        {
            foreach (var field in file.Layout.Fields)
            {
                var element = view.Structure.Find(field.Name)!;
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
    }
    internal IReadOnlyDictionary<string, object?> SendRecord(NativeView view, RpgDiskBinding file)
    {
        var image = NativeBuffer(view).ToArray(); var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in file.Layout.Fields)
        {
            var element = view.Structure.Find(field.Name)!;
            result.Add(field.External, ReadNative(new(view.Structure, image, element.Offset, element.StorageLength, element, false, 1)));
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
            file.Layout.Fields.ToDictionary(field => field.External, field => context.ReadValue(field.Name), StringComparer.OrdinalIgnoreCase);
}
