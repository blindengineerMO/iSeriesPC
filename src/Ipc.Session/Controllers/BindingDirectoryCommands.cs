using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private (string Library, string Name) ResolveBindingDirectory(string text, AuthorityBit required)
    {
        var (library, name) = SplitQualified(CommandParser.Unquote(text), LibraryUnknown); var store = new BindingDirectoryStore(_system.Connections);
        foreach (var candidate in SearchLibraries(library))
            if (store.Find(candidate, name, required) is not null) return (candidate, name);
        throw new CpfException("CPF9801", "Binding directory not found: " + text);
    }
    private static IReadOnlyList<string> BindingEntryTerms(string text)
    {
        text = text.Trim(); if (text.StartsWith('(') && text.EndsWith(')')) text = text[1..^1].Trim();
        return CommandParser.Tokenize(text).Select(t => CommandParser.Unquote(t).ToUpperInvariant()).ToArray();
    }
    private static IReadOnlyList<string> BindingEntryList(CommandCall call)
    {
        var tokens = call.Split("OBJ");
        if (tokens.Count is 2 or 3 && tokens[1].StartsWith('*') && !tokens[0].StartsWith('(')) return [call.GetOption("OBJ")!];
        return tokens;
    }
    private BindingDirectoryEntry ParseBindingEntry(string text)
    {
        var terms = BindingEntryTerms(text);
        if (terms.Count is < 1 or > 3) throw new CpfException("IPC0003", "OBJ entries use (library/object type activation).");
        var (library, name) = SplitQualified(terms[0], LibraryUnknown);
        if (library == "*CURLIB") library = SearchLibraries(library).First();
        var entry = new BindingDirectoryEntry(library, name, terms.Count > 1 ? terms[1] : ObjectType.ServiceProgram, terms.Count > 2 ? terms[2] : "*IMMED"); entry.Validate(); return entry;
    }
    private CommandResult ExecuteBindingDirectory(CommandCall call)
    {
        var text = CommandParser.Unquote(call.GetOption("BNDDIR"));
        if (text.Length == 0) return CommandResult.Error(call.Name + " requires BNDDIR(name).");
        try
        {
            var store = new BindingDirectoryStore(_system.Connections); var operation = call.Name.ToUpperInvariant();
            if (operation == "CRTBNDDIR")
            {
                var (library, name) = SplitQualified(text, "*CURLIB"); if (library == "*CURLIB") library = SearchLibraries(library).First();
                store.Create(library, name, CommandParser.Unquote(call.GetOption("TEXT")), CommandParser.Unquote(call.GetOption("AUT") ?? "*LIBCRTAUT").ToUpperInvariant());
                return CommandResult.Ok($"Binding directory {library}/{name} created.");
            }
            var required = operation switch { "ADDBNDDIRE" => AuthorityBit.ObjectOperate | AuthorityBit.Add, "RMVBNDDIRE" => AuthorityBit.ObjectOperate | AuthorityBit.Delete, "DLTBNDDIR" => AuthorityBit.ObjectExist, _ => Authorities.UseBits };
            var key = ResolveBindingDirectory(text, required);
            if (operation == "DLTBNDDIR")
            {
                _system.Objects.Delete(key.Library, key.Name, ObjectType.BindingDirectory); return CommandResult.Ok($"Binding directory {key.Library}/{key.Name} deleted.");
            }
            if (operation == "DSPBNDDIR")
            {
                var entries = store.Read(key.Library, key.Name);
                return new CommandResult { Message = $"Binding directory {key.Library}/{key.Name}: {entries.Count} entries.",
                    Listing = entries.Select((e, i) => $"{i + 1}: {e.Library}/{e.Name} {e.Type} {e.Activation}").ToArray() };
            }
            var items = BindingEntryList(call);
            if (operation == "ADDBNDDIRE")
            {
                var position = call.Split("POSITION").Select(CommandParser.Unquote).Select(t => t.ToUpperInvariant()).ToArray();
                var mode = position.Length == 0 ? "*LAST" : position[0]; BindingDirectoryEntry? anchor = null;
                if (position.Length > 1) anchor = ParseBindingEntry(string.Join(' ', position.Skip(1)));
                store.Add(key.Library, key.Name, items.Select(ParseBindingEntry).ToArray(), mode, anchor);
            }
            else
            {
                var selectors = items.Select(item =>
                {
                    var terms = BindingEntryTerms(item); if (terms.Count is < 1 or > 2) throw new CpfException("IPC0003", "Removal entries use (library/object type).");
                    var (library, name) = SplitQualified(terms[0], LibraryUnknown);
                    if (library == "*CURLIB") library = SearchLibraries(library).First();
                    return new BindingDirectorySelector(library, name, terms.Count > 1 ? terms[1] : "*ALL");
                }).ToArray(); store.Remove(key.Library, key.Name, selectors);
            }
            return CommandResult.Ok($"Binding directory {key.Library}/{key.Name} updated.");
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException) { return CommandResult.Error("IPC0003: " + error.Message); }
    }
}
