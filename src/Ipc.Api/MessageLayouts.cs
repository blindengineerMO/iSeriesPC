using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Messages;

namespace Ipc.Api;

public sealed partial class MessageApi
{
    private sealed record Content(byte[] Bytes, int Ccsid, int Status, bool Text = false);

    private byte[] ReceiveLayout(QueuedMessage? message, int length, string format, bool program, bool remove, int outputCcsid, ProgramBuffer previous)
    {
        if (message is null) { var empty = new byte[8]; Put(empty, 0, 8); return empty; }
        var headerLength = format == "RCVM0100" ? 48 : 176;
        var header = new byte[headerLength]; var definition = message.Predefined;
        Content data; Content text; Content help;
        if (definition is null)
        {
            data = ConvertText(message.Data, outputCcsid, Math.Max(0, length - headerLength));
            text = new([], ccsid, 0); help = text;
        }
        else
        {
            var convertible = definition.Description.Fields.Any(f => f.Type == "*CCHAR");
            var original = definition.Replacement;
            data = new(original.ToArray(), convertible ? original.Ccsid : 65535, outputCcsid == 65535 || !convertible ? 1 : 0);
            if (convertible && outputCcsid != 65535 && outputCcsid != original.Ccsid)
            {
                try
                {
                    var converted = MessageDescriptionFormat.ConvertReplacementData(definition.Description, original, outputCcsid);
                    data = converted.Length <= Math.Max(0, length - headerLength) ? new(converted.ToArray(), outputCcsid, 3) : data with { Status = 2 };
                }
                catch (CpfException) { data = data with { Status = -1 }; }
            }
            text = new([], definition.Description.Ccsid, 0); help = text;
            if (format != "RCVM0100")
            {
                var sourceText = MessageDescriptionFormat.Format(definition.Description, original, definition.Description.Ccsid, convertCharacterData: false);
                var status = outputCcsid == 65535 ? 1 : 0;
                text = new(sourceText.Text.ToArray(), definition.Description.Ccsid, status, true);
                help = new(sourceText.SecondLevel.ToArray(), definition.Description.Ccsid, status, true);
                if (outputCcsid != 65535)
                {
                    try
                    {
                        var converted = MessageDescriptionFormat.Format(definition.Description, original, outputCcsid);
                        status = definition.Description.Ccsid != outputCcsid || convertible && original.Ccsid != outputCcsid ? 3 : 0;
                        if (status == 0 || converted.Text.Length + converted.SecondLevel.Length <= Math.Max(0, length - headerLength - data.Bytes.Length))
                        { text = new(converted.Text.ToArray(), outputCcsid, status, true); help = new(converted.SecondLevel.ToArray(), outputCcsid, status, true); }
                        else { text = text with { Status = 2 }; help = help with { Status = 2 }; }
                    }
                    catch (CpfException) { text = text with { Status = -1 }; help = help with { Status = -1 }; }
                }
            }
        }
        if (message.MessageId.Length > 0) Put(header, 8, message.Severity);
        else if (previous.Length > 8) previous.ToArray().AsSpan(8, Math.Min(4, previous.Length - 8)).CopyTo(header.AsSpan(8));
        Text(header, 12, 7, message.MessageId);
        var returnType = message.ReturnType ?? throw new CpfException("IPC0006", "Message type has no native receive representation.");
        Text(header, 19, 2, !program && returnType is "16" or "17" ? returnType == "16" ? "14" : "15" : returnType);
        if (remove) Text(header, 21, 4, "");
        else BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(21), message.Kind == "RPY" && message.CorrelationKey is { } copy ? copy : message.Key);
        if (format == "RCVM0100")
        {
            Text(header, 25, 7, ""); Put(header, 32, data.Status); Put(header, 36, data.Ccsid);
            return Information(header, length, [(40, 44, data)]);
        }
        Text(header, 25, 10, definition?.File ?? ""); Text(header, 35, 10, definition?.RequestedLibrary ?? "");
        Text(header, 45, 10, message.ActualMessageFileLibrary ?? "");
        var origin = message.Origin; var stamp = (origin?.Sent ?? message.Sent).UtcDateTime;
        Text(header, 55, 10, origin?.JobName ?? ""); Text(header, 65, 10, origin?.JobUser ?? "");
        Text(header, 75, 6, message.SenderJob?.ToString("D6", CultureInfo.InvariantCulture) ?? "");
        Text(header, 81, 12, origin?.Program ?? ""); Text(header, 93, 4, "");
        Text(header, 97, 7, (stamp.Year / 100 - 19).ToString(CultureInfo.InvariantCulture) + stamp.ToString("yyMMdd", CultureInfo.InvariantCulture));
        Text(header, 104, 6, stamp.ToString("HHmmss", CultureInfo.InvariantCulture));
        if (program)
        {
            Text(header, 110, 10, origin?.Recipient ?? ""); Text(header, 120, 4, "");
            Text(header, 124, 1, string.IsNullOrEmpty(origin?.Program) ? "" : "0");
            Text(header, 125, 1, string.IsNullOrEmpty(origin?.Recipient) ? "" : "0"); Text(header, 126, 1, "");
        }
        else
        {
            Text(header, 110, 6, ((stamp.Ticks % TimeSpan.TicksPerSecond) / 10).ToString("D6", CultureInfo.InvariantCulture));
            Text(header, 116, 10, message.Sender); Text(header, 126, 1, "");
        }
        Put(header, 127, Math.Max(text.Status, help.Status)); Put(header, 131, data.Status); Text(header, 135, 9, "*NO");
        Put(header, 144, text.Ccsid); Put(header, 148, data.Ccsid);
        return Information(header, length, [(152, 156, data), (160, 164, text), (168, 172, help)]);
    }

    private Content ConvertText(ProgramBuffer buffer, int outputCcsid, int budget)
    {
        var source = new Content(buffer.ToArray(), buffer.Ccsid, outputCcsid == 65535 ? 1 : 0, Text: true);
        if (outputCcsid == 65535 || buffer.Ccsid == outputCcsid) return source;
        try
        {
            var encoding = (Encoding)CodePage.FromCcsid(outputCcsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            var converted = encoding.GetBytes(buffer.ToText());
            return converted.Length <= budget ? new(converted, outputCcsid, 3, Text: true) : source with { Status = 2 };
        }
        catch (Exception error) when (error is CpfException or EncoderFallbackException) { return source with { Status = -1 }; }
    }
    private byte[] Information(byte[] header, int length, IReadOnlyList<(int Returned, int Available, Content Data)> fields)
    {
        var available = checked(header.Length + fields.Sum(f => f.Data.Bytes.Length));
        using var output = new MemoryStream(); output.Write(header);
        foreach (var field in fields)
        {
            var count = Math.Min(field.Data.Bytes.Length, Math.Max(0, length - (int)output.Length));
            if (field.Data.Text && field.Data.Ccsid == 1208 && count < field.Data.Bytes.Length)
            {
                var encoding = new UTF8Encoding(false, true);
                try
                {
                    _ = encoding.GetCharCount(field.Data.Bytes);
                    while (count > 0)
                    {
                        try { _ = encoding.GetCharCount(field.Data.Bytes.AsSpan(0, count)); break; }
                        catch (DecoderFallbackException) { count--; }
                    }
                }
                catch (DecoderFallbackException) { } // Unconverted impromptu bytes remain opaque.
            }
            Put(header, field.Returned, count); Put(header, field.Available, field.Data.Bytes.Length);
            output.Write(field.Data.Bytes.AsSpan(0, count));
        }
        var returned = Math.Min(length, (int)output.Length); Put(header, 0, returned); Put(header, 4, available);
        var result = output.ToArray().AsSpan(0, returned).ToArray(); header.AsSpan(0, Math.Min(header.Length, returned)).CopyTo(result); return result;
    }
    private static void Put(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), value);
    private void Text(byte[] bytes, int offset, int width, string value)
    {
        var data = _arguments.Encode(value);
        if (data.Length > width) throw new CpfException("IPC0136", "Message metadata exceeds its native field.");
        bytes.AsSpan(offset, width).Fill(_arguments.Encode(" ")[0]); data.CopyTo(bytes, offset);
    }

    private void Retrieve(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error)
    {
        var receiver = _arguments.CharacterOutput(p[0]); var length = _arguments.Binary32(p[1]);
        if (length < 8 || length > receiver.Buffer.Length) throw new CpfException("CPF3C24", "Invalid retrieve information length.");
        Distinct(error, receiver.Target);
        if (_arguments.Text(p[2], 8) != "RTVM0100") throw new CpfException("CPF3C21", "Unsupported retrieve format.");
        var identifier = _arguments.Text(p[3], 7); var (library, name, _) = MessageFile(p[4]);
        var replacementLength = _arguments.Binary32(p[6]);
        if (replacementLength is < 0 or > 512) throw new CpfException("CPF24B6", "Unsupported replacement data length.");
        var replace = _arguments.Text(p[7], 10); var controls = _arguments.Text(p[8], 10);
        if (replace is not ("*YES" or "*NO") || controls is not ("*YES" or "*NO")) throw ApiArguments.Bad("Invalid retrieve formatting option.");
        var option = p.Count == 13 ? _arguments.Text(p[10], 10) : "*MSGID";
        if (option is not ("*MSGID" or "*FIRST" or "*NEXT")) throw ApiArguments.Bad("Invalid retrieve option.");
        var outputCcsid = p.Count == 13 ? Ccsid(p[11]) : ccsid;
        if (outputCcsid == 65535) throw new CpfException("IPC0006", "RTVM0100 requires a supported text output CCSID.");
        var inputCcsid = p.Count == 13 ? Ccsid(p[12], binary: false) : ccsid;
        var data = _arguments.Character(p[5], replacementLength).ToArray(); var replacement = new ProgramBuffer(data.AsSpan(0, replacementLength), inputCcsid);
        var descriptions = new MessageDescriptionStore(factory).List(library, name);
        var selected = option switch { "*FIRST" => descriptions.FirstOrDefault(), "*NEXT" => descriptions.FirstOrDefault(d => MessageDescriptionStore.CompareIdentifiers(d.Key, identifier) > 0),
            _ => descriptions.FirstOrDefault(d => d.Key == identifier) };
        ApiArguments.Assignment output;
        if (selected.Value is null)
        {
            if (option == "*MSGID") throw new CpfException("CPF2419", "Message description was not found.");
            output = _arguments.Prefix(receiver, Enumerable.Repeat(_arguments.Encode(" ")[0], length).ToArray(), length);
        }
        else
        {
            var description = selected.Value;
            ProgramBuffer text, help;
            if (replace == "*YES")
            {
                var formatted = MessageDescriptionFormat.Format(description, replacement, outputCcsid); text = formatted.Text; help = formatted.SecondLevel;
            }
            else
            {
                var encoding = (Encoding)CodePage.FromCcsid(outputCcsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                try { text = new(encoding.GetBytes(description.Text), outputCcsid); help = new(encoding.GetBytes(description.SecondLevel), outputCcsid); }
                catch (EncoderFallbackException) { throw new CpfException("CPF247E", "Message description cannot be converted."); }
            }
            if (controls == "*NO") help = new(CodePage.ToBytes(outputCcsid, Regex.Replace(help.ToText(), "&[NPB]", "", RegexOptions.CultureInvariant)), outputCcsid);
            var header = new byte[24];
            var bytes = Information(header, length, [(8, 12, new(text.ToArray(), text.Ccsid, 0, true)), (16, 20, new(help.ToArray(), help.Ccsid, 0, true))]);
            output = _arguments.Prefix(receiver, bytes, bytes.Length);
        }
        var success = error.Success(); cancellationToken.ThrowIfCancellationRequested(); Apply(output, success);
    }
}
