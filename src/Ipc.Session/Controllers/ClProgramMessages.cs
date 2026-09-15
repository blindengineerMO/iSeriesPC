using Ipc.Cl.Interpreter;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Commands;
using Ipc.Services.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private ProgramMessageReference? SendClProgramMessage(ClStatement statement, string text, ClCommandContext context)
    {
        var call = statement.Command;
        var requiresQueue = statement.MessageType == "*INQ" || call?.GetOption("TOPGMQ") is { } target && target != "*PRV" || call?.GetOption("TOMSGQ") is not null || call?.GetOption("KEYVAR") is not null;
        if (_job is null)
        {
            if (requiresQueue)
                throw new CpfException("CPF1241", "Program message queues require an executing job.");
            return null; // Trusted host execution retains the legacy output sink.
        }
        var key = ResolveCommandObject("SNDPGMMSG");
        var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
        if (definition.Builtin != "SNDPGMMSG") throw new CpfException("IPC0003", "Compiled SNDPGMMSG requires the built-in command definition.");
        var environment = _system.JobRuntime.Environment(_job.Key);
        if (environment is null)
        {
            if (requiresQueue) throw new CpfException("CPF1241", "Program message queues require a job execution owner.");
            return null;
        }
        string Option(string name, string fallback) => context.Resolve(call?.GetOption(name) ?? fallback).Trim().ToUpperInvariant();
        ProgramArgument? output = null;
        if (call?.GetOption("KEYVAR") is { } variable)
        {
            output = context.Variable(variable.Trim());
            if (output.Type != "*CHAR" || output.Length != 4) throw new CpfException("IPC0003", "KEYVAR requires a four-byte CHAR variable.");
        }
        var store = new MessageQueueStore(_system.Connections);
        MessageQueueAddress Named(string name)
        {
            if (name == "*SYSOPR") return new(new QualifiedName("QSYS", "QSYSOPR"));
            if (name.StartsWith('*')) throw new CpfException("IPC0003", "Unsupported message destination.");
            var (library, objectName) = ResolveWorkObject(name, ObjectType.MessageQueue); return new(new QualifiedName(library, objectName));
        }
        var destination = Option("TOMSGQ", "*TOPGMQ"); var relationship = Option("TOPGMQ", "*PRV");
        var queues = destination == "*TOPGMQ" ? new[] { environment.MessageQueue(relationship) }
            : CommandParser.Tokenize(destination).Select(Named).ToArray();
        MessageQueueAddress? reply = null;
        if (statement.MessageType == "*INQ")
        {
            if (destination == "*TOPGMQ" && relationship != "*EXT") throw new CpfException("IPC0003", "Inquiries require a named or external queue.");
            var replyName = Option("RPYMSGQ", "*PGMQ"); reply = replyName == "*PGMQ" ? environment.MessageQueue() : Named(replyName);
        }
        else if (call?.GetOption("RPYMSGQ") is not null) throw new CpfException("IPC0003", "RPYMSGQ requires an inquiry.");
        var data = new ProgramBuffer(StrictJobEncoding(_job.Ccsid).GetBytes(text), _job.Ccsid);
        if (statement.MessageId is null && data.Length > 3000) throw new CpfException("IPC0003", "Compiled immediate program messages allow at most 3000 bytes.");
        var keys = store.SendTo(queues, data, statement.MessageType[1..], reply, messageId: statement.MessageId ?? "", severity: statement.MessageId is null ? 0 : 40,
            cancellationToken: _cancellationToken, senderCopy: statement.MessageType == "*INQ");
        if (output is not null)
        {
            var bytes = new byte[4];
            if (statement.MessageType == "*INQ" || destination == "*TOPGMQ") System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, keys[0]);
            else bytes.AsSpan().Fill(StrictJobEncoding(_job.Ccsid).GetBytes(" ")[0]);
            output.Value = new ProgramBuffer(bytes, _job.Ccsid);
        }
        return statement.MessageType == "*ESCAPE" ? new ProgramMessageReference(queues[0].ProgramQueue!, keys[0]) : null;
    }

    private CommandResult ReportClFailure(CommandResult failure, bool outgoing)
    {
        if (_job is null || _system.JobRuntime.Environment(_job.Key) is not { } environment) return failure;
        var destination = environment.MessageQueue(outgoing ? "*PRV" : "*SAME");
        // Host diagnostics can include Unicode paths even in an EBCDIC job. Store
        // bounded, valid UTF-8 and let RCVMSG perform its requested conversion.
        var text = failure.MessageData ?? failure.Message ?? failure.MessageId ?? "Program failed.";
        var bytes = new byte[4096];
        System.Text.Encoding.UTF8.GetEncoder().Convert(text.AsSpan(), bytes.AsSpan(), true, out _, out var length, out _);
        var reference = new MessageQueueStore(_system.Connections).DeliverException(destination,
            new ProgramBuffer(bytes.AsSpan(0, length).ToArray(), 1208), failure.MessageId ?? "IPC0006", failure.ExceptionReference, _cancellationToken);
        return CommandResult.Error(failure.Message ?? "Program failed.", failure.MessageId, failure.MessageData, reference);
    }
}
