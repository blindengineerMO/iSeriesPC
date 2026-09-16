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
    private ClProgramMessage SendClProgramMessage(ClStatement statement, object value, ClCommandContext context)
    {
        var call = statement.Command;
        var key = ResolveCommandObject("SNDPGMMSG");
        var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
        if (definition.Builtin != "SNDPGMMSG") throw new CpfException("IPC0003", "Compiled SNDPGMMSG requires the built-in command definition.");

        var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        var replacement = call?.GetOption("MSGDTA") is { } input && (input.TrimStart().StartsWith('&') || input.Equals("*NONE", StringComparison.OrdinalIgnoreCase))
            ? MessageReplacementData(input, context)
            : value is ProgramBuffer raw ? raw : new ProgramBuffer(StrictJobEncoding(ccsid).GetBytes(ClExpression.Text(value)), ccsid);
        var messageId = statement.MessageId is null ? "" : context.Resolve(statement.MessageId).Trim().ToUpperInvariant();
        PredefinedMessage? predefined = null;
        var data = replacement; var severity = messageId.Length == 0 ? 0 : 40; ProgramBuffer? defaultReply = null;
        if (messageId.Length > 0)
        {
            var fileName = context.Resolve(call?.GetOption("MSGF") ?? "QCPFMSG").Trim().ToUpperInvariant();
            var (requestedLibrary, requestedName) = SplitQualified(fileName, "*LIBL");
            var found = SearchLibraries(requestedLibrary).FirstOrDefault(library => _system.Objects.Exists(library, requestedName, ObjectType.MessageFile));
            // Preserve the existing CPF9898 immediate-text adapter when no real QCPFMSG exists.
            if (!(found is null && messageId == "CPF9898" && fileName is "QCPFMSG" or "QSYS/QCPFMSG"))
            {
                if (found is null) throw new CpfException("CPF2407", "Message file not found.");
                predefined = new MessageDescriptionStore(_system.Connections).Snapshot(found, requestedName, messageId, requestedLibrary, replacement);
                var formatted = MessageDescriptionFormat.Format(predefined.Description, replacement, ccsid);
                data = formatted.Text; severity = formatted.Severity; defaultReply = formatted.DefaultReply;
            }
        }
        string text;
        try { text = data.ToText(); } catch (CpfException) { text = "X'" + Convert.ToHexString(data.ToArray()) + "'"; }
        var prepared = new ClProgramMessage(text, messageId.Length == 0 ? null : messageId, messageId.Length == 0 ? null : replacement);
        var requiresQueue = statement.MessageType == "*INQ" || call?.GetOption("TOPGMQ") is { } target && target != "*PRV" || call?.GetOption("TOMSGQ") is not null || call?.GetOption("KEYVAR") is not null;
        if (_job is null)
        {
            if (requiresQueue)
                throw new CpfException("CPF1241", "Program message queues require an executing job.");
            return prepared; // Trusted host execution retains the legacy output sink.
        }
        var environment = _system.JobRuntime.Environment(_job.Key);
        if (environment is null)
        {
            if (requiresQueue) throw new CpfException("CPF1241", "Program message queues require a job execution owner.");
            return prepared;
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
            : CommandParser.Tokenize(call!.GetOption("TOMSGQ")!).Select(value => Named(context.Resolve(value).Trim().ToUpperInvariant())).ToArray();
        MessageQueueAddress? reply = null;
        if (statement.MessageType == "*INQ")
        {
            if (destination == "*TOPGMQ" && relationship != "*EXT") throw new CpfException("IPC0003", "Inquiries require a named or external queue.");
            var replyName = Option("RPYMSGQ", "*PGMQ"); reply = replyName == "*PGMQ" ? environment.MessageQueue() : Named(replyName);
        }
        else if (call?.GetOption("RPYMSGQ") is not null) throw new CpfException("IPC0003", "RPYMSGQ requires an inquiry.");
        if (messageId.Length == 0 && data.Length > 3000) throw new CpfException("IPC0003", "Compiled immediate program messages allow at most 3000 bytes.");
        var keys = store.SendTo(queues, data, statement.MessageType[1..], reply, statement.MessageType == "*INQ" ? defaultReply : null, messageId: messageId, severity: severity,
            cancellationToken: _cancellationToken, senderCopy: statement.MessageType == "*INQ", predefined: predefined);
        if (output is not null)
        {
            var bytes = new byte[4];
            if (statement.MessageType == "*INQ" || destination == "*TOPGMQ") System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, keys[0]);
            else bytes.AsSpan().Fill(StrictJobEncoding(_job.Ccsid).GetBytes(" ")[0]);
            output.Value = new ProgramBuffer(bytes, _job.Ccsid);
        }
        return prepared with { Reference = statement.MessageType == "*ESCAPE" ? new ProgramMessageReference(queues[0].ProgramQueue!, keys[0]) : null };
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
        return CommandResult.Error(failure.Message ?? "Program failed.", failure.MessageId, failure.MessageData, reference, failure.MessageDataBuffer);
    }
}
