using System.Buffers.Binary;
using System.Text.RegularExpressions;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Api;

/// <summary>Message-handler program ABI over the job's existing queue and description services.</summary>
public sealed partial class MessageApi(SqliteConnectionFactory factory, Func<string, IEnumerable<string>> searchLibraries,
    int ccsid = 37, JobCallEnvironment? environment = null, JobKey? job = null, CancellationToken cancellationToken = default)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private MessageQueueStore Messages => new(factory);
    private sealed record PreparedMessage(string Id, ProgramBuffer Text, PredefinedMessage? Definition, int Severity);
    public void Invoke(string name, IReadOnlyList<object?> parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (counts, errorIndex) = name switch {
            "QMHSNDPM" => (new[] { 9, 12, 14 }, 8), "QMHRCVPM" => (new[] { 10, 12, 14, 15 }, 9),
            "QMHSNDM" => (new[] { 10, 11 }, 9), "QMHRCVM" => (new[] { 9, 10, 11 }, 8),
            "QMHSNDRM" => (new[] { 6, 7, 8 }, 5), "QMHRTVM" => (new[] { 10, 13 }, 9),
            _ => throw new CpfException("IPC0136", "Unknown message API.") };
        if (!counts.Contains(parameters.Count)) throw new CpfException("CPF3C36", "Incomplete message API parameter group.");
        var error = new ApiArguments.ErrorReceiver(_arguments, parameters[errorIndex]);
        ApiArguments.DistinctErrorReceiver(error, parameters, errorIndex);
        try
        {
            switch (name)
            {
                case "QMHSNDPM": SendProgram(parameters, error); break;
                case "QMHRCVPM": Receive(parameters, error, program: true); break;
                case "QMHSNDM": SendNamed(parameters, error); break;
                case "QMHRCVM": Receive(parameters, error, program: false); break;
                case "QMHSNDRM": Reply(parameters, error); break;
                case "QMHRTVM": Retrieve(parameters, error); break;
            }
        }
        catch (CpfException failure) when (error.Report(failure)) { }
    }
    private void SendProgram(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error)
    {
        var kind = _arguments.Text(p[4], 10);
        if (kind is not ("*INFO" or "*COMP" or "*DIAG"))
            throw new CpfException("IPC0006", "Program API exception, notify, status, request and terminal inquiry delivery is not implemented.");
        var destination = ProgramQueue(p[5], p[6], p.Count >= 12 ? p[9] : null, p.Count >= 12 ? p[10] : null,
            p.Count == 14 ? p[12] : null);
        if (p.Count >= 12 && _arguments.Binary32(p[11]) != 0)
            throw new CpfException("IPC0006", "Nonzero Display Program Messages screen waits are not implemented.");
        var inputCcsid = p.Count == 14 ? Ccsid(p[13], binary: false) : ccsid;
        var message = PrepareMessage(p[0], p[1], p[2], p[3], inputCcsid);
        var key = _arguments.CharacterOutput(p[7]);
        if (key.Buffer.Length < 4) throw ApiArguments.Bad("Message key output requires four bytes.");
        Distinct(error, key.Target); var success = error.Success(); ApiArguments.Assignment? output = null;
        Messages.SendTo([destination], message.Text, kind[1..], messageId: message.Id, severity: message.Severity,
            cancellationToken: cancellationToken, predefined: message.Definition, prepare: keys =>
            { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, keys[0]); output = _arguments.Prefix(key, bytes, 4); });
        Apply(output!, success);
    }
    private void SendNamed(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error)
    {
        var kind = _arguments.Text(p[4], 10);
        if (kind is not ("*INFO" or "*COMP" or "*DIAG" or "*INQ")) throw new CpfException("CPF24B3", "Invalid nonprogram message type.");
        var count = _arguments.Binary32(p[6]);
        if (count is < 1 or > 50 || kind == "*INQ" && count != 1) throw ApiArguments.Bad("Invalid message queue count.");
        var names = _arguments.Character(p[5], count * 20).ToArray(); var destinations = new List<MessageQueueAddress>();
        for (var n = 0; n < count; n++) destinations.Add(NamedQueue(new ProgramBuffer(names.AsSpan(n * 20, 20), ccsid)));
        if (destinations.Distinct().Count() != count) throw ApiArguments.Bad("Duplicate destination message queues.");
        var message = PrepareMessage(p[0], p[1], p[2], p[3], p.Count == 11 ? Ccsid(p[10], binary: false) : ccsid);
        var reply = kind == "*INQ" ? NamedQueue(p[7], reply: true) : null;
        var key = kind == "*INQ" ? _arguments.CharacterOutput(p[8]) : null;
        if (key is not null && key.Buffer.Length < 4) throw ApiArguments.Bad("Message key output requires four bytes.");
        Distinct(error, key is null ? [] : [key.Target]); var success = error.Success(); ApiArguments.Assignment? output = null;
        Messages.SendTo(destinations, message.Text, kind[1..], reply, messageId: message.Id, severity: message.Severity,
            cancellationToken: cancellationToken, senderCopy: kind == "*INQ", predefined: message.Definition,
            prepare: keys => { if (key is null) return; var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, keys[0]); output = _arguments.Prefix(key, bytes, 4); });
        Apply(output, success);
    }
    private void Receive(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error, bool program)
    {
        var receiver = _arguments.CharacterOutput(p[0]); var length = _arguments.Binary32(p[1]);
        if (length < 8 || length > receiver.Buffer.Length) throw new CpfException("CPF24A7", "Invalid message information length.");
        Distinct(error, receiver.Target);
        var format = _arguments.Text(p[2], 8);
        if (format is not ("RCVM0100" or "RCVM0200")) throw new CpfException("CPF3C21", "Unsupported receive format.");
        var source = program ? ProgramQueue(p[3], p[4], p.Count >= 12 ? p[10] : null, p.Count >= 12 ? p[11] : null, p.Count >= 14 ? p[12] : null) : NamedQueue(p[3]);
        var type = _arguments.Text(p[program ? 5 : 4], 10); var key = Key(p[program ? 6 : 5]);
        var seconds = _arguments.Binary32(p[program ? 7 : 6]);
        if (seconds < -1) throw new CpfException("CPF24A8", "Invalid message wait time.");
        var wait = seconds == -1 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(seconds);
        var action = _arguments.Text(p[program ? 8 : 7], 10);
        if (action is not ("*OLD" or "*REMOVE" or "*SAME")) throw new CpfException("CPF24A9", "Invalid message action.");
        if (program && p.Count == 15 && _arguments.Text(p[14], 10) != "*NO" || !program && p.Count == 11 && _arguments.Text(p[10], 10) != "*NO")
            throw new CpfException("IPC0006", "Reply rejection exit programs are not implemented.");
        var outputCcsid = program ? p.Count >= 14 ? Ccsid(p[13]) : ccsid : p.Count >= 10 ? Ccsid(p[9]) : ccsid;
        if (key.Top && type != "*NEXT") throw new CpfException("CPF24B2", "*TOP requires *NEXT.");
        if (type is "*FIRST" or "*LAST" && !key.Blank) throw new CpfException("CPF24AF", "Message key is not allowed.");
        if (type is "*NEXT" or "*PRV" or "*COPY" && key.Blank) throw new CpfException("CPF24B1", "A message key is required.");
        if (key.Number == 0 && !key.Blank && !key.Top && type is not ("*NEXT" or "*PRV"))
            throw new CpfException("CPF2551", "Invalid message type and key combination.");
        var kind = type switch { "*INFO" => "INFO", "*COMP" => "COMP", "*DIAG" => "DIAG", "*RPY" => "RPY", "*COPY" => "COPY", "*INQ" when !program => "INQ",
            "*ESCAPE" when program => "ESCAPE", "*NOTIFY" when program => "NOTIFY", "*EXCP" when program => "EXCP", _ => null };
        if (kind is null && type is not ("*ANY" or "*FIRST" or "*LAST" or "*NEXT" or "*PRV"))
            throw new CpfException("CPF24B3", "Unsupported receive message type.");
        var selection = type switch { "*FIRST" => MessageSelection.First, "*LAST" => MessageSelection.Last,
            "*NEXT" => MessageSelection.After, "*PRV" => key.Number == 0 ? MessageSelection.Last : MessageSelection.Before,
            "*EXCP" when key.Blank => MessageSelection.ExceptionNewest, _ => key.Blank ? MessageSelection.Next : MessageSelection.Key };
        if (type == "*COPY" && key.Number == 0) throw new CpfException("CPF2551", "A sender copy requires its key.");
        var success = error.Success(); ApiArguments.Assignment? output = null;
        if (!program && (!key.Blank && type != "*RPY" || key.Blank && type == "*RPY")) wait = TimeSpan.Zero;
        using var lease = !program && wait != TimeSpan.Zero ? Messages.AllocateReceiver(source, job ?? throw new CpfException("CPF1241", "A waiting receive requires a job."), cancellationToken) : null;
        Messages.ReceiveFrom(source, selection, key.Number, remove: action == "*REMOVE", wait: wait, cancellationToken: cancellationToken, kind: kind,
            preserveState: action == "*SAME", requireKeyExists: !key.Blank && !key.Top && key.Number != 0,
            prepare: received =>
            {
                if (received is null && selection == MessageSelection.Key && type != "*RPY") throw new CpfException("CPF2410", "Requested message key or type was not found.");
                var bytes = ReceiveLayout(received, length, format, program, action == "*REMOVE", outputCcsid, receiver.Buffer);
                output = _arguments.Prefix(receiver, bytes, bytes.Length);
            }, formatPredefined: false);
        Apply(output!, success);
    }
    private void Reply(IReadOnlyList<object?> p, ApiArguments.ErrorReceiver error)
    {
        var key = Key(p[0]); if (key.Blank || key.Top || key.Number == 0) throw new CpfException("CPF2410", "An inquiry key is required.");
        var queue = NamedQueue(p[1], external: true);
        var length = _arguments.Binary32(p[3]); if (length is < 1 or > 132) throw new CpfException("CPF24B6", "Invalid reply text length.");
        var inputCcsid = p.Count >= 7 ? Ccsid(p[6], binary: false) : ccsid;
        var bytes = _arguments.Character(p[2], length).ToArray().AsSpan(0, length).ToArray();
        var reply = bytes.All(b => b == CodePage.ToBytes(inputCcsid, " ")[0]) ? null : new ProgramBuffer(bytes, inputCcsid);
        var remove = _arguments.Text(p[4], 10) switch { "*YES" => true, "*NO" => false, _ => throw ApiArguments.Bad("Invalid remove message option.") };
        if (p.Count == 8 && _arguments.Text(p[7], 10) != "*NO") throw new CpfException("IPC0006", "Reply rejection exit programs are not implemented.");
        var success = error.Success(); Messages.ReplyTo(queue, key.Number, reply, remove, cancellationToken); Apply(null, success);
    }
    private PreparedMessage PrepareMessage(object? identifier, object? file, object? data, object? size, int inputCcsid)
    {
        var id = _arguments.Text(identifier, 7); var length = _arguments.Binary32(size);
        if (length < (id.Length == 0 ? 1 : 0) || length > (id.Length == 0 ? 6000 : 512))
            throw new CpfException("CPF24B6", "Message exceeds the supported text or replacement data length.");
        var raw = _arguments.Character(data, length).ToArray(); var input = new ProgramBuffer(raw.AsSpan(0, length), inputCcsid);
        if (id.Length == 0) return new("", input, null, 0);
        if (!Regex.IsMatch(id, @"\A[A-Z][A-Z0-9]{2}[0-9A-F]{4}\z", RegexOptions.CultureInvariant)) throw new CpfException("CPF2419", "Invalid message identifier.");
        var (library, name, requested) = MessageFile(file);
        var definition = new MessageDescriptionStore(factory).Snapshot(library, name, id, requested, input);
        var formatted = MessageDescriptionFormat.Format(definition.Description, input, ccsid);
        return new(id, formatted.Text, definition, formatted.Severity);
    }
    private MessageQueueAddress ProgramQueue(object? name, object? counter, object? length, object? qualification, object? type)
    {
        if (environment is null) throw new CpfException("CPF1241", "A program message API requires a job call environment.");
        var width = length is null ? 10 : _arguments.Binary32(length);
        if (width is < 1 or > 4096) throw new CpfException("CPF24B7", "Invalid call stack entry length.");
        if (type is not null && _arguments.Text(type, 10) != "*CHAR") throw new CpfException("IPC0006", "Pointer call stack entries are not implemented.");
        if (qualification is not null)
        {
            var bytes = _arguments.Character(qualification, 20).ToArray();
            if (new ProgramBuffer(bytes.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ') != "*NONE" || new ProgramBuffer(bytes.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ') != "*NONE")
                throw new CpfException("IPC0006", "Qualified ILE call stack entries are not implemented.");
        }
        var entry = _arguments.Text(name, width);
        if (entry is not ("*" or "*EXT") && !ObjectName.IsValid(entry)) throw new CpfException("IPC0006", "Unsupported call stack selector.");
        return environment.ApiMessageQueue(entry, _arguments.Binary32(counter));
    }
    private MessageQueueAddress NamedQueue(object? value, bool reply = false, bool external = false)
    {
        var bytes = _arguments.Character(value, 20).ToArray();
        var name = new ProgramBuffer(bytes.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ');
        var library = new ProgramBuffer(bytes.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ');
        if (reply && name == "*PGMQ" && library.Length == 0) return environment?.ApiMessageQueue("*", 0) ?? throw new CpfException("CPF1241", "A program reply queue requires a job.");
        if (external && name == "*EXT" && library.Length == 0) return environment?.ApiMessageQueue("*EXT", 0) ?? throw new CpfException("CPF1241", "The external queue requires a job.");
        var resolved = Resolve(name, library, ObjectType.MessageQueue, "CPF2403"); return new(new QualifiedName(resolved.Library, name));
    }
    private (string Library, string Name, string Requested) MessageFile(object? value)
    {
        var bytes = _arguments.Character(value, 20).ToArray();
        var name = new ProgramBuffer(bytes.AsSpan(0, 10), ccsid).ToText().TrimEnd(' ');
        var requested = new ProgramBuffer(bytes.AsSpan(10, 10), ccsid).ToText().TrimEnd(' ');
        return (Resolve(name, requested, ObjectType.MessageFile, "CPF2407").Library, name, requested);
    }
    private QualifiedName Resolve(string name, string library, string objectType, string missing)
    {
        if (!ObjectName.IsValid(name) || !ObjectName.IsValid(library) && library is not ("*LIBL" or "*CURLIB"))
            throw new CpfException("IPC0006", "Unsupported message object selector.");
        foreach (var candidate in library is "*LIBL" or "*CURLIB" ? searchLibraries(library) : [library])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (new SqliteObjectStore(factory).Get(candidate, name, objectType) is { }) return new(candidate, name);
        }
        throw new CpfException(missing, "Message object was not found.");
    }
    private (bool Blank, bool Top, uint Number) Key(object? value)
    {
        var bytes = _arguments.Character(value, 4).ToArray().AsSpan(0, 4);
        var blank = bytes.SequenceEqual(_arguments.Encode("    ")); var top = bytes.SequenceEqual(_arguments.Encode("*TOP"));
        return (blank, top, blank || top ? 0 : BinaryPrimitives.ReadUInt32BigEndian(bytes));
    }
    private int Ccsid(object? value, bool binary = true)
    {
        var selected = _arguments.Binary32(value); if (selected == 0) return ccsid;
        if (binary && selected == 65535) return selected;
        if (!CodePage.IsSupported(selected)) throw new CpfException("IPC0006", "Unsupported message data CCSID.");
        return selected;
    }
    private static void Distinct(ApiArguments.ErrorReceiver error, params ProgramArgument[] outputs)
        => ApiArguments.DistinctOutputs(error.Target is { } target ? outputs.Append(target) : outputs);
    private static void Apply(ApiArguments.Assignment? value, ApiArguments.Assignment? error)
        => ApiArguments.Apply(new[] { value, error }.OfType<ApiArguments.Assignment>());
}
