using System.Buffers.Binary;
using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Menu;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterMessageCommands()
    {
        foreach (var name in new[] { "CRTMSGQ", "DLTMSGQ", "SNDMSG", "SNDRPY", "DSPMSG", "RMVMSG" })
            _catalog.Register(name, call => ExecuteMessageCommand(call, null));
        _catalog.Register("RCVMSG", _ => CommandResult.Error("RCVMSG requires a compiled CL variable frame."));
        _catalog.Register("SNDPGMMSG", _ => CommandResult.Error("SNDPGMMSG requires a compiled CL variable frame."));
    }
    private CommandResult ExecuteMessageCommand(CommandCall call, ClCommandContext? context)
    {
        var store = new MessageQueueStore(_system.Connections); var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        string Text(string key, string fallback = "") => (context?.Resolve(call.GetOption(key) ?? fallback) ?? CommandParser.Unquote(call.GetOption(key) ?? fallback)).TrimEnd();
        string Choice(string key, string fallback = "") => Text(key, fallback).ToUpperInvariant();
        QualifiedName Queue(string value, bool create = false)
        {
            value = value.Trim().ToUpperInvariant();
            if (value == "*SYSOPR") return new("QSYS", "QSYSOPR");
            if (value.StartsWith('*')) throw new CpfException("IPC0003", "A named MSGQ is required by this command implementation.");
            var (library, name) = ResolveWorkObject(value.ToUpperInvariant(), ObjectType.MessageQueue, creating: create); return new(library, name);
        }
        ProgramBuffer Input(string keyword, int maximum)
        {
            var raw = call.GetOption(keyword) ?? throw new CpfException("IPC0003", keyword + " is required.");
            ProgramBuffer value;
            if (context is not null && raw.TrimStart().StartsWith('&'))
            {
                var argument = context.Variable(raw.Trim());
                if (argument.Type != "*CHAR") throw new CpfException("IPC0003", keyword + " requires character data.");
                value = argument.ToBuffer()!;
            }
            else if (raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase) && raw.EndsWith('\''))
            {
                var hex = raw[2..^1];
                if (hex.Length % 2 != 0 || hex.Any(c => !Uri.IsHexDigit(c))) throw new CpfException("IPC0003", keyword + " requires an even number of hexadecimal digits.");
                value = new(Convert.FromHexString(hex), ccsid);
            }
            else value = new(StrictJobEncoding(ccsid).GetBytes(CommandParser.Unquote(raw)), ccsid);
            if (value.Length > maximum) throw new CpfException("IPC0003", keyword + " exceeds " + maximum + " bytes.");
            return value;
        }
        uint Key()
        {
            var buffer = Input("MSGKEY", 4);
            if (buffer.Length != 4) throw new CpfException("IPC0003", "MSGKEY requires four bytes.");
            var key = BinaryPrimitives.ReadUInt32BigEndian(buffer.ToArray());
            if (key == 0) throw new CpfException("IPC0003", "MSGKEY must be nonzero.");
            return key;
        }
        bool Remove() => Choice("RMV", "*YES") switch { "*YES" => true, "*NO" => false, _ => throw new CpfException("IPC0003", "RMV must be *YES or *NO.") };
        if (call.Name == "CRTMSGQ")
        {
            var queue = Queue(Choice("MSGQ"), true); store.Create(queue, Text("TEXT"), Choice("AUT", "*LIBCRTAUT"));
            return CommandResult.Ok("Message queue " + queue + " created.");
        }
        if (call.Name == "DLTMSGQ")
        {
            var queue = Queue(Choice("MSGQ")); _system.Objects.Delete(queue.Library, queue.Name.Value, ObjectType.MessageQueue);
            return CommandResult.Ok("Message queue " + queue + " deleted.");
        }
        if (call.Name == "SNDMSG")
        {
            var destinations = call.Split("TOMSGQ").Select(q => Queue(context?.Resolve(q) ?? CommandParser.Unquote(q))).ToArray();
            var type = Choice("MSGTYPE", "*INFO");
            if (type is not ("*INFO" or "*INQ")) throw new CpfException("IPC0003", "SNDMSG supports *INFO and *INQ.");
            var reply = call.GetOption("RPYMSGQ") is null ? (QualifiedName?)null : Queue(Choice("RPYMSGQ"));
            store.Send(destinations, Input("MSG", 512), type[1..], reply, cancellationToken: _cancellationToken);
            return CommandResult.Ok("Message sent.");
        }
        if (call.Name == "SNDRPY")
        {
            var defaultReply = call.GetOption("RPY") is null || call.GetOption("RPY")!.Trim().Equals("*DFT", StringComparison.OrdinalIgnoreCase);
            store.Reply(Queue(Choice("MSGQ")), Key(), defaultReply ? null : Input("RPY", 132), Remove(), _cancellationToken);
            return CommandResult.Ok("Reply sent.");
        }
        if (call.Name == "DSPMSG")
        {
            var queue = Queue(Choice("MSGQ", "*WRKUSR"));
            var messages = store.List(queue, limit: 1000);
            return WorkResult("Messages for " + queue, "DSPMSG MSGQ(" + queue + ")", messages.Select(message =>
            {
                var key = "X'" + message.Key.ToString("X8", CultureInfo.InvariantCulture) + "'";
                var actions = new List<WorkAction> {
                    new("5", "Display", "", Details: new[] {
                        "Message key: " + key, "Type: " + message.Kind + "   ID: " + message.MessageId + "   Severity: " + message.Severity,
                        "Sender: " + message.Sender + "   Job: " + message.SenderJob,
                        "Sent: " + message.Sent.ToString("O"), "CCSID: " + message.Data.Ccsid,
                        DisplayEncodedText(message.Data) }),
                    new("4", "Remove", "RMVMSG MSGQ(" + queue + ") MSGKEY(" + key + ")", Confirm: true)
                };
                if (message.Kind == "INQ" && !message.Replied)
                    actions.Insert(0, new("2", "Reply", "SNDRPY MSGQ(" + queue + ") MSGKEY(" + key + ")", Prompt: true));
                return new WorkRow(key, message.Kind + " " + message.Sender + " " + DisplayEncodedText(message.Data), actions);
            }));
        }
        if (call.Name == "RMVMSG")
        {
            var queueName = Choice("MSGQ", "*PGMQ");
            if (queueName != "*PGMQ" && call.GetOption("PGMQ") is not null) throw new CpfException("IPC0003", "PGMQ requires MSGQ(*PGMQ).");
            var clear = Choice("CLEAR", "*BYKEY") switch {
                "*BYKEY" => MessageClear.ByKey, "*ALL" => MessageClear.All, "*KEEPUNANS" => MessageClear.KeepUnanswered,
                "*OLD" => MessageClear.Old, "*NEW" => MessageClear.New, _ => throw new CpfException("IPC0003", "Invalid CLEAR selection.") };
            if (clear != MessageClear.ByKey && call.GetOption("MSGKEY") is { } rawKey && !rawKey.Equals("*NONE", StringComparison.OrdinalIgnoreCase)) throw new CpfException("IPC0003", "MSGKEY requires CLEAR(*BYKEY).");
            // CL frames currently follow OPM semantics; RMVEXCP applies to ILE exceptions.
            if (Choice("RMVEXCP", "*YES") is not ("*YES" or "*NO")) throw new CpfException("IPC0003", "RMVEXCP must be *YES or *NO.");
            int removed;
            if (queueName == "*PGMQ" && Choice("PGMQ", "*SAME") == "*ALLINACT")
            {
                if (clear != MessageClear.All) throw new CpfException("IPC0003", "PGMQ(*ALLINACT) requires CLEAR(*ALL).");
                if (_job is null) throw new CpfException("CPF1241", "An executing job is required.");
                removed = store.RemoveInactiveJobMessages(_job.Key, _cancellationToken);
            }
            else
            {
                var queue = queueName == "*PGMQ" ? EnvironmentForJob().MessageQueue(Choice("PGMQ", "*SAME")) : new MessageQueueAddress(Queue(queueName));
                removed = store.RemoveFrom(queue, clear, clear == MessageClear.ByKey ? Key() : 0, _cancellationToken);
            }
            return CommandResult.Ok(removed.ToString(CultureInfo.InvariantCulture) + " messages removed.");
        }
        if (context is null) throw new CpfException("IPC0003", "RCVMSG requires a compiled CL variable frame.");
        var source = Choice("MSGQ", "*PGMQ") == "*PGMQ" ? EnvironmentForJob().MessageQueue(Choice("PGMQ", "*SAME")) : new MessageQueueAddress(Queue(Choice("MSGQ")));
        if (source.Named is not null && call.GetOption("PGMQ") is not null) throw new CpfException("IPC0003", "PGMQ requires MSGQ(*PGMQ).");
        var senderFormat = Choice("SENDERFMT", "*SHORT");
        if (senderFormat is not ("*SHORT" or "*LONG") || call.GetOption("SENDERFMT") is not null && call.GetOption("SENDER") is null)
            throw new CpfException("IPC0003", "SENDERFMT requires SENDER and must be *SHORT or *LONG.");
        var outputs = new Dictionary<string, ProgramArgument>();
        foreach (var keyword in new[] { "MSG", "MSGLEN", "KEYVAR", "MSGID", "SEV", "TXTCCSID", "RTNTYPE", "SENDER", "SECLVL", "SECLVLLEN", "MSGDTA", "MSGDTALEN", "MSGF", "MSGFLIB", "SNDMSGFLIB", "DTACCSID" })
        {
            if (call.GetOption(keyword) is not { } variable) continue;
            if (!variable.Trim().StartsWith('&')) throw new CpfException("IPC0003", keyword + " requires a return variable.");
            var argument = context.Variable(variable.Trim());
            var numericLength = keyword switch { "SEV" => 2, "MSGLEN" or "TXTCCSID" or "SECLVLLEN" or "MSGDTALEN" or "DTACCSID" => 5, _ => 0 };
            if (numericLength > 0 ? argument.Type != "*DEC" || argument.Length != numericLength || argument.Decimals != 0
                : argument.Type != "*CHAR" || argument.Length < (keyword == "SENDER" ? senderFormat == "*LONG" ? 720 : 80 : keyword == "MSGID" ? 7 : keyword is "MSGF" or "MSGFLIB" or "SNDMSGFLIB" ? 10 : keyword == "KEYVAR" ? 4 : 1) || keyword == "KEYVAR" && argument.Length != 4 || keyword == "RTNTYPE" && argument.Length != 2)
                throw new CpfException("IPC0003", "Invalid return variable type or length for " + keyword + ".");
            outputs.Add(keyword, argument);
        }
        var typeChoice = Choice("MSGTYPE", "*ANY");
        // MSGKEY is an opaque buffer; only decode known literal selectors.
        var keyChoice = call.GetOption("MSGKEY")?.TrimStart().StartsWith('&') == true ? "&KEY" : Choice("MSGKEY", "*NONE");
        var key = keyChoice is "*NONE" or "*TOP" ? 0 : Key();
        var selection = typeChoice switch {
            "*ANY" => keyChoice == "*NONE" ? MessageSelection.Next : MessageSelection.Key,
            "*FIRST" => MessageSelection.First, "*LAST" => MessageSelection.Last,
            "*EXCP" when source.ProgramQueue is not null && keyChoice == "*NONE" => MessageSelection.ExceptionNewest,
            "*NEXT" => MessageSelection.After, "*PRV" => MessageSelection.Before,
            "*INFO" or "*INQ" or "*RPY" or "*COMP" or "*DIAG" or "*COPY" => keyChoice == "*NONE" ? MessageSelection.Next : MessageSelection.Key,
            _ => throw new CpfException("IPC0003", "Unsupported RCVMSG message selector.") };
        if (typeChoice is "*NEXT" or "*PRV" && keyChoice == "*NONE" || keyChoice == "*TOP" && typeChoice != "*NEXT" || typeChoice is "*FIRST" or "*LAST" && keyChoice != "*NONE")
            throw new CpfException("IPC0003", "Invalid MSGTYPE and MSGKEY combination.");
        var waitText = Choice("WAIT", "0");
        var wait = waitText == "*MAX" ? Timeout.InfiniteTimeSpan : int.TryParse(waitText, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 0 and <= 604800 ? TimeSpan.FromSeconds(seconds) : throw new CpfException("IPC0003", "WAIT must be 0–604800 seconds or *MAX.");
        var receiveCcsid = Choice("CCSID", "*JOB") switch { "*JOB" => (int?)ccsid, "*HEX" => null, var number => int.TryParse(number, out var id) ? id : throw new CpfException("IPC0003", "Invalid CCSID.") };
        if (source.ProgramQueue is not null && wait != TimeSpan.Zero && typeChoice != "*RPY") throw new CpfException("IPC0003", "Program message queues can wait only for replies.");
        var removal = Choice("RMV", "*YES");
        if (removal is not ("*YES" or "*NO" or "*KEEPEXCP")) throw new CpfException("IPC0003", "Invalid RMV selection.");
        var senderCopy = selection == MessageSelection.Key && store.ListFrom(source, key - 1, 1).FirstOrDefault() is { Kind: "COPY" } copy && copy.Key == key;
        var received = store.ReceiveFrom(source, selection, key, removal == "*YES", wait, _cancellationToken,
            typeChoice is "*INFO" or "*INQ" or "*RPY" or "*COMP" or "*DIAG" or "*COPY" ? typeChoice[1..] : null, receiveCcsid,
            requireReturnType: outputs.ContainsKey("RTNTYPE"), keepException: removal == "*KEEPEXCP",
            senderLength: outputs.TryGetValue("SENDER", out var sender) ? sender.Length : 0, longSender: senderFormat == "*LONG", senderCcsid: ccsid);
        if (received is null && selection == MessageSelection.Key && !(senderCopy && typeChoice is "*ANY" or "*RPY")) throw new CpfException("CPF2410", "Requested message key or type was not found.");
        var keyed = received is { Kind: "RPY", CorrelationKey: { } replyKey } && typeChoice is "*ANY" or "*RPY"
            ? received with { Key = replyKey } : received;
        var replacement = received?.ReplacementData ?? (received?.MessageId.Length > 0 ? received.Data : null);
        foreach (var (keyword, target) in outputs)
        {
            if (target.Type == "*DEC")
            {
                target.Value = (decimal)(keyword switch { "MSGLEN" => received?.Data.Length ?? 0, "SEV" => received?.Severity ?? 0,
                    "SECLVLLEN" => received?.SecondLevel?.Length ?? 0, "MSGDTALEN" => replacement?.Length ?? 0,
                    "DTACCSID" => received is null || received.MessageId.Length == 0 ? 0 : received.Predefined?.Description.Fields.Any(field => field.Type == "*CCHAR") == true ? replacement!.Ccsid : 65535,
                    _ => received?.Data.Ccsid ?? 0 });
                continue;
            }
            var bytes = new byte[target.Length]; bytes.AsSpan().Fill(StrictJobEncoding(ccsid).GetBytes(" ")[0]);
            var value = keyword switch { "MSG" => received?.Data.ToArray(), "KEYVAR" => keyed?.KeyBuffer(ccsid).ToArray(),
                "SENDER" => received?.SenderInformation?.ToArray(),
                "SECLVL" => received?.SecondLevel?.ToArray(), "MSGDTA" => replacement?.ToArray(),
                "MSGF" => StrictJobEncoding(ccsid).GetBytes(received?.Predefined?.File ?? ""),
                "MSGFLIB" => StrictJobEncoding(ccsid).GetBytes(received?.Predefined?.RequestedLibrary ?? ""),
                "SNDMSGFLIB" => StrictJobEncoding(ccsid).GetBytes(received?.ActualMessageFileLibrary ?? ""),
                "RTNTYPE" => received is null ? null : StrictJobEncoding(ccsid).GetBytes(received.ReturnType!),
                _ => received is null ? null : StrictJobEncoding(ccsid).GetBytes(received.MessageId) };
            if (value is not null) value.AsSpan(0, Math.Min(value.Length, bytes.Length)).CopyTo(bytes);
            target.Value = new ProgramBuffer(bytes, ccsid);
        }
        return CommandResult.Ok();
    }
}
