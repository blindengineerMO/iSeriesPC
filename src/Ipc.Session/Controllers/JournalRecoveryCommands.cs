using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Objects;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteJournalRecovery(CommandCall call, bool remove)
    {
        string Value(string keyword, string fallback = "") => CommandParser.Unquote(call.GetOption(keyword) ?? fallback).ToUpperInvariant();
        QualifiedName Object(string value, string type) { var name = ResolveWorkObject(CommandParser.Unquote(value).ToUpperInvariant(), type); return new(name.Library, name.Name); }
        var journal = Object(Value("JRN"), ObjectType.Journal);
        var honorBoundaries = Value("CMTBDY", "*YES") switch { "*YES" => true, "*NO" => false, _ => throw JournalParameter("CMTBDY requires *YES or *NO.") };
        if (Value("OUTPUT", "*NONE") != "*NONE") throw JournalParameter("Recovery outfile formats are not implemented; specify OUTPUT(*NONE).");
        var policy = Value("OBJERROPT", "*CONTINUE") switch { "*CONTINUE" => JournalRecoveryErrorPolicy.Continue, "*END" => JournalRecoveryErrorPolicy.End, _ => throw JournalParameter("OBJERROPT requires *CONTINUE or *END.") };
        var tokens = call.Split("FILE"); if (tokens.Count == 0) throw JournalParameter("Specify FILE with physical file/member selections.");
        var lists = tokens.Any(token => token.StartsWith('(')) ? tokens.Select(token => token.StartsWith('(') && token.EndsWith(')') ? CommandParser.Tokenize(token[1..^1]) : throw JournalParameter("Invalid FILE element list.")).ToArray() : [tokens];
        var files = lists.Select(parts => parts.Count is < 1 or > 2 ? throw JournalParameter("Each FILE element requires one file and optional member.") : new JournalFileSelection(Object(parts[0], ObjectType.File), parts.Count == 1 ? "*ALL" : CommandParser.Unquote(parts[1]).ToUpperInvariant())).ToArray();
        QualifiedName? first = null, last = null;
        IReadOnlyList<string> receivers = call.GetOption("RCVRNG") is null && remove ? ["*CURRENT"] : call.Split("RCVRNG");
        if (receivers.Count == 1 && CommandParser.Unquote(receivers[0]).Equals("*CURRENT", StringComparison.OrdinalIgnoreCase)) { }
        else if (receivers.Count == 2) { first = Object(receivers[0], ObjectType.JournalReceiver); last = !remove && CommandParser.Unquote(receivers[1]).Equals("*CURRENT", StringComparison.OrdinalIgnoreCase) ? null : Object(receivers[1], ObjectType.JournalReceiver); }
        else throw JournalParameter("Specify RCVRNG(*CURRENT) or two receiver names; save-derived receiver ranges are not implemented.");
        long? Sequence(string name, string special)
        {
            var normal = call.GetOption(name); var large = call.GetOption(name + "LRG");
            if (normal is null && large is null && remove) return null;
            if ((normal is null) == (large is null)) throw JournalParameter("Specify exactly one of " + name + " and " + name + "LRG; save/restore-derived bounds are unavailable.");
            var value = CommandParser.Unquote(normal ?? large!).ToUpperInvariant(); if (value == special) return null;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 1 || normal is not null && sequence > 9999999999) throw JournalParameter("Invalid " + name + " sequence; large values require " + name + "LRG.");
            return sequence;
        }
        var result = _system.Journals.Recover(journal, files, Sequence("FROMENT", remove ? "*LAST" : "*FIRST"), Sequence("TOENT", remove ? "*FIRST" : "*LAST"), remove, first, last, _cancellationToken, policy, honorBoundaries);
        var message = $"{result.Changes} record changes {(remove ? "removed" : "applied")}. First {result.FirstSequence}; last {result.LastSequence}.";
        var diagnostics = result.Boundaries?.Select(boundary => "Incomplete commit cycle " + boundary.Cycle + ": stopped before sequence " + boundary.StoppedBeforeSequence + "; requested end " + boundary.RequestedEndSequence + ".").ToArray();
        if (result.Failures is not { Count: > 0 } failures)
            return diagnostics is { Length: > 0 } ? new() { Message = message + " Commitment boundary reached before requested end.", Listing = diagnostics } : CommandResult.Ok(message);
        return new() { Outcome = CommandOutcome.Error, MessageId = "CPF70A7", Message = message + " Recovery ended for " + failures.Count + " member(s).",
            Listing = failures.Select(failure => failure.File + " member " + failure.Member + " sequence " + failure.Sequence + ": " + failure.Message).Concat(diagnostics ?? []).ToArray() };
    }
}
