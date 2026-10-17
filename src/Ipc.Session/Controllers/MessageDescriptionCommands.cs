using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private void RegisterMessageDescriptionCommands()
    {
        foreach (var name in new[] { "CRTMSGF", "DLTMSGF", "ADDMSGD", "CHGMSGD", "RMVMSGD", "DSPMSGD", "RTVMSG" })
            _catalog.Register(name, call => ExecuteMessageDescription(call));
    }
    private CommandResult ExecuteMessageDescription(CommandCall call, ClCommandContext? context = null)
    {
        string Resolve(string value) => context?.Resolve(value) ?? CommandParser.Unquote(value);
        string Option(string name, string fallback = "") => Resolve(call.GetOption(name) ?? fallback).Trim().ToUpperInvariant();
        var creating = call.Name == "CRTMSGF";
        var (library, name) = ResolveWorkObject(Option("MSGF"), ObjectType.MessageFile, creating: creating);
        var store = new MessageDescriptionStore(_system.Connections);
        var jobCcsid = _job?.Ccsid ?? _system.Config.Ccsid;
        int Ccsid(string keyword, int hexValue)
        {
            var value = Option(keyword, "*JOB");
            if (value == "*JOB") return jobCcsid;
            if (value == "*HEX") return hexValue;
            if (!int.TryParse(value, out var ccsid) || !CodePage.IsSupported(ccsid)) throw new CpfException("IPC0003", "Unsupported " + keyword + ".");
            return ccsid;
        }
        if (creating)
        {
            if (Option("CCSID", "*JOB") == "*HEX") throw new CpfException("IPC0003", "Untagged message files are not supported.");
            store.Create(library, name, Resolve(call.GetOption("TEXT") ?? ""), Ccsid("CCSID", jobCcsid));
            return CommandResult.Ok("Message file " + library + "/" + name + " created.");
        }
        if (call.Name == "DLTMSGF") { _system.Objects.Delete(library, name, ObjectType.MessageFile); return CommandResult.Ok("Message file deleted."); }
        var id = Option("MSGID", call.Name == "DSPMSGD" ? "*ALL" : "");
        if (call.Name == "RMVMSGD") { store.Remove(library, name, id); return CommandResult.Ok("Message descriptions removed."); }
        if (call.Name == "DSPMSGD")
        {
            var entries = id == "*ALL" ? store.List(library, name) : new[] { new KeyValuePair<string, MessageDescription>(id, store.GetDescription(library, name, id)) };
            return new() { Listing = entries.SelectMany(pair => new[] {
                pair.Key + "  Severity " + pair.Value.Severity + "  " + pair.Value.Text, pair.Value.SecondLevel,
                pair.Value.ReplyRules is { } rules ? "Reply: " + rules.Type + " LEN(" + (rules.Length == 0 ? "*TYPE" : rules.Length == -1 ? "*NONE" : rules.Length + " " + rules.Decimals) + ")" : "",
                pair.Value.ReplyRules?.Values is { Count: > 0 } values ? "Values: " + string.Join(", ", values) : "",
                pair.Value.ReplyRules?.SpecialValues is { Count: > 0 } special ? "Special values: " + string.Join(", ", special.Select(value => value.From + " -> " + (value.To ?? value.From))) : "",
                pair.Value.ReplyRules?.Range is { } range ? "Range: " + range.Lower + " through " + range.Upper : "",
                pair.Value.ReplyRules?.Relation is { } relation ? "Relation: " + relation.Operator + " " + relation.Value : ""
            }).Where(line => line.Length > 0).ToArray() };
        }
        if (call.Name == "RTVMSG")
        {
            if (context is null) throw new CpfException("IPC0003", "RTVMSG requires a compiled CL variable frame.");
            var targets = new Dictionary<string, ProgramArgument>();
            foreach (var keyword in new[] { "MSG", "MSGLEN", "SECLVL", "SECLVLLEN", "SEV", "TXTCCSID", "DTACCSID" })
            {
                if (call.GetOption(keyword) is not { } variable) continue;
                if (!variable.Trim().StartsWith('&')) throw new CpfException("IPC0003", keyword + " requires a return variable.");
                var target = context.Variable(variable.Trim());
                var numeric = keyword is "MSG" or "SECLVL" ? 0 : keyword == "SEV" ? 2 : 5;
                if (numeric == 0 ? target.Type != "*CHAR" || target.Length < 1 : target.Type != "*DEC" || target.Length != numeric || target.Decimals != 0)
                    throw new CpfException("IPC0003", "Invalid return variable for " + keyword + ".");
                targets.Add(keyword, target);
            }
            var description = store.GetDescription(library, name, id);
            var outputCcsid = Ccsid("CCSID", description.Ccsid); var dataCcsid = Ccsid("MDTACCSID", outputCcsid);
            var data = MessageReplacementData(call.GetOption("MSGDTA") ?? "''", context);
            var convert = Option("CCSID", "*JOB") != "*HEX" && Option("MDTACCSID", "*JOB") != "*HEX";
            var result = MessageDescriptionFormat.Format(description, new ProgramBuffer(data.ToArray(), dataCcsid), outputCcsid, convert);
            var assignments = new List<(ProgramArgument Target, object? Value)>();
            foreach (var (keyword, target) in targets)
            {
                object value;
                if (keyword is "MSG" or "SECLVL")
                {
                    var bytes = (keyword == "MSG" ? result.Text : result.SecondLevel).ToArray();
                    var padded = Enumerable.Repeat(StrictJobEncoding(outputCcsid).GetBytes(" ")[0], target.Length).ToArray();
                    bytes.AsSpan(0, Math.Min(bytes.Length, padded.Length)).CopyTo(padded); value = new ProgramBuffer(padded, jobCcsid);
                }
                else value = (decimal)(keyword switch { "MSGLEN" => result.Text.Length, "SECLVLLEN" => result.SecondLevel.Length,
                    "SEV" => result.Severity, "TXTCCSID" => outputCcsid,
                    _ => description.Fields.Any(field => field.Type == "*CCHAR") ? convert ? outputCcsid : dataCcsid : 65535 });
                assignments.Add((target, target.Normalize(value)));
            }
            foreach (var assignment in assignments) assignment.Target.Value = assignment.Value;
            return CommandResult.Ok();
        }
        string? Changed(string keyword)
        {
            var raw = call.GetOption(keyword);
            return raw is null || raw.Equals("*SAME", StringComparison.OrdinalIgnoreCase) ? null : Resolve(raw);
        }
        var adding = call.Name == "ADDMSGD";
        string? Text(string keyword, string? fallback = null)
        {
            var value = adding ? call.GetOption(keyword) : Changed(keyword);
            if (value is null) return fallback;
            return adding ? Resolve(value) : value;
        }
        var first = Text("MSG"); var second = Text("SECLVL", adding ? "" : null);
        if (call.GetOption("SECLVL")?.Equals("*NONE", StringComparison.OrdinalIgnoreCase) == true) second = "";
        var severityText = adding ? call.GetOption("SEV") ?? "0" : Changed("SEV");
        int? severity = null;
        if (severityText is not null) severity = int.TryParse(Resolve(severityText), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : throw new CpfException("IPC0003", "Invalid message severity.");
        var formats = adding ? call.GetOption("FMT") ?? "*NONE" : Changed("FMT");
        var fields = formats is null ? null : MessageDescriptionFormat.Parse(formats);
        var defaultReply = Text("DFT");
        if (call.GetOption("DFT")?.Equals("*NONE", StringComparison.OrdinalIgnoreCase) == true) defaultReply = null;
        var replyUpdate = ParseMessageReplyUpdate(call, Resolve, adding);
        if (adding)
        {
            if (first is null) throw new CpfException("IPC0003", "ADDMSGD requires MSG.");
            store.Add(library, name, id, new MessageDescription(first, second!, severity!.Value, fields!, jobCcsid, defaultReply,
                ReplyRules: replyUpdate?.Apply(null)));
        }
        else store.Change(library, name, id, first, second, severity, fields, defaultReply,
            call.GetOption("DFT") is { } supplied && !supplied.Equals("*SAME", StringComparison.OrdinalIgnoreCase), replyUpdate);
        return CommandResult.Ok("Message description " + id + " updated.");
    }

    private static MessageReplyUpdate? ParseMessageReplyUpdate(CommandCall call, Func<string, string> resolve, bool adding)
    {
        string? Raw(string keyword)
        {
            var raw = call.GetOption(keyword);
            return !adding && raw?.Equals("*SAME", StringComparison.OrdinalIgnoreCase) == true ? null : raw;
        }
        var keywords = new[] { "TYPE", "LEN", "VALUES", "SPCVAL", "RANGE", "REL" };
        if (keywords.All(keyword => Raw(keyword) is null)) return null;
        CpfException Invalid() => new("IPC0003", "Invalid message reply parameter list.");
        bool None(string text) => text.Equals("*NONE", StringComparison.OrdinalIgnoreCase);
        string[] Elements(string text) => CommandParser.Tokenize(text).Select(resolve).ToArray();
        int Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : throw Invalid();
        var type = Raw("TYPE") is { } rawType ? resolve(rawType).ToUpperInvariant() : adding ? "*CHAR" : null;
        int? length = adding ? 0 : null; int? decimals = adding ? 0 : null;
        if (Raw("LEN") is { } rawLength)
        {
            var elements = Elements(rawLength);
            if (elements.Length is < 1 or > 2) throw Invalid();
            length = elements[0].ToUpperInvariant() switch { "*NONE" => -1, "*TYPE" => 0, _ => Number(elements[0]) };
            if (length <= 0 && (elements.Length != 1 || elements[0].ToUpperInvariant() is not ("*TYPE" or "*NONE"))) throw Invalid();
            decimals = elements.Length == 2 ? Number(elements[1]) : 0;
        }
        IReadOnlyList<string>? values = Raw("VALUES") is not { } rawValues ? null : None(rawValues) ? Array.Empty<string>() : Elements(rawValues);
        if (Raw("VALUES") is { } suppliedValues && !None(suppliedValues) && values?.Count is not (>= 1 and <= 20)) throw Invalid();
        IReadOnlyList<MessageReplyValue>? special = null;
        if (Raw("SPCVAL") is { } rawSpecial)
        {
            var entries = new List<MessageReplyValue>();
            if (!None(rawSpecial))
            {
                var separated = new System.Text.StringBuilder(); var quoted = false; var depth = 0;
                foreach (var character in rawSpecial)
                {
                    separated.Append(character);
                    if (character == '\'') quoted = !quoted;
                    else if (!quoted && character == '(') depth++;
                    else if (!quoted && character == ')' && --depth == 0) separated.Append(' ');
                }
                var tokens = CommandParser.Tokenize(separated.ToString());
                if (tokens.Count is < 1 or > 20) throw Invalid();
                foreach (var token in tokens)
                {
                    if (!token.StartsWith('(') || !token.EndsWith(')')) throw Invalid();
                    var elements = Elements(token[1..^1]);
                    if (elements.Length is < 1 or > 2) throw Invalid();
                    entries.Add(new(elements[0], elements.Length == 2 ? elements[1] : null));
                }
            }
            special = entries;
        }
        MessageReplyRange? range = null;
        if (Raw("RANGE") is { } rawRange && !None(rawRange))
        {
            var elements = Elements(rawRange); if (elements.Length != 2) throw Invalid();
            range = new(elements[0], elements[1]);
        }
        MessageReplyRelation? relation = null;
        if (Raw("REL") is { } rawRelation && !None(rawRelation))
        {
            var elements = Elements(rawRelation); if (elements.Length != 2) throw Invalid();
            relation = new(elements[0].ToUpperInvariant(), elements[1]);
        }
        return new(type, length, decimals, values, special, Raw("RANGE") is not null, range, Raw("REL") is not null, relation);
    }

    private ProgramBuffer MessageReplacementData(string raw, ClCommandContext? context)
    {
        raw = raw.Trim(); var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        if (raw.Equals("*NONE", StringComparison.OrdinalIgnoreCase)) return new(Array.Empty<byte>(), ccsid);
        if (context is not null && raw.StartsWith('&'))
        {
            var variable = context.Variable(raw);
            if (variable.Type != "*CHAR") throw new CpfException("IPC0003", "MSGDTA requires CHAR storage.");
            return variable.ToBuffer() ?? throw new CpfException("IPC0003", "MSGDTA has no byte storage.");
        }
        if (raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase))
            return (ProgramBuffer)ClExpression.Compile(raw).Evaluate(_ => throw new ClRuntimeException("Expected hexadecimal message data."), ccsid);
        return new(StrictJobEncoding(ccsid).GetBytes(CommandParser.Unquote(raw)), ccsid);
    }
}
