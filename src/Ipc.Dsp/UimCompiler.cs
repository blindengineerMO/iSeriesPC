using System.Text;
using System.Text.RegularExpressions;
using Ipc.Core.Objects;
using Ipc.Terminal;

namespace Ipc.Dsp;

/// <summary>Declarative help only: links can display a help module but cannot execute commands or programs.</summary>
public sealed class UimCompiler
{
    public static bool ValidModule(string name) => name.Length is >= 1 and <= 32 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_');
    public HelpDefinition Compile(string name, string source, string sourceName = "<UIM>")
    {
        var offsets = new List<int> { 0 }; for (var i = 0; i < source.Length; i++) if (source[i] == '\n') offsets.Add(i + 1);
        UimCompileException Error(int offset, string tag, string message)
        {
            var line = offsets.BinarySearch(offset); if (line < 0) line = ~line - 1;
            return new(sourceName, line + 1, offset - offsets[line] + 1, tag, message);
        }
        if (!ObjectName.IsValid(name) || source.Length > 1024 * 1024 || offsets.Count > 10000) throw Error(0, "SOURCE", "Invalid name or source exceeds 1 MiB/10000 lines.");
        var characters = source.ToCharArray();
        foreach (var offset in offsets)
        {
            var end = source.IndexOf('\n', offset); if (end < 0) end = source.Length;
            var line = source[offset..end];
            if (line.TrimStart().StartsWith(".*", StringComparison.Ordinal)) Array.Fill(characters, ' ', offset, end - offset);
        }
        var input = new string(characters); var definition = new HelpDefinition { Name = name };
        HelpModule? module = null; HelpBlock? block = null; HelpLink? link = null;
        var style = "PLAIN"; var title = new StringBuilder(); var started = false; var ended = false; var tags = 0; var linkId = 0;
        void FinishTitle()
        {
            if (module is null || block is not null) return;
            module.Title = Normalize(title.ToString()); title.Clear();
            if (module.Title.Length > 120) throw Error(0, "HELP", "Help title exceeds 120 characters.");
        }
        string Normalize(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        string Decode(string text, int offset)
        {
            text = Regex.Replace(text, @"&([A-Za-z]+)\.", match => match.Groups[1].Value.ToLowerInvariant() switch {
                "amp" => "&", "colon" => ":", "period" => ".", "apos" => "'", "quot" => "\"", "lt" => "<", "gt" => ">",
                _ => throw Error(offset + match.Index, match.Value, "Unsupported symbol.") }, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (text.Any(c => !char.IsWhiteSpace(c) && !TerminalGlyph.IsSingleCell(c))) throw Error(offset, "TEXT", "Help requires printable single-cell text.");
            return Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        for (var position = 0; position < input.Length;)
        {
            var offset = position;
            if (input[position] != ':' || position + 1 >= input.Length || !char.IsAsciiLetter(input[position + 1]))
            {
                position++;
                while (position < input.Length && !(input[position] == ':' && position + 1 < input.Length && char.IsAsciiLetter(input[position + 1]))) position++;
                var text = Decode(input[offset..position], offset);
                if (module is null) { if (!string.IsNullOrWhiteSpace(text)) throw Error(offset, "TEXT", "Text must be inside HELP."); }
                else if (block is null) title.Append(text);
                else if (text.Length > 0) block.Spans.Add(new(text, style, link));
                continue;
            }
            if (++tags > 8192) throw Error(offset, "SOURCE", "Too many UIM tags.");
            position++; var start = position;
            while (position < input.Length && char.IsAsciiLetterOrDigit(input[position])) position++;
            var tag = input[start..position].ToUpperInvariant();
            var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
            while (true)
            {
                while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
                if (position >= input.Length) throw Error(offset, tag, "Missing tag-ending period.");
                if (input[position] == '.') { position++; break; }
                var keyStart = position;
                while (position < input.Length && (char.IsAsciiLetterOrDigit(input[position]) || input[position] == '_')) position++;
                if (position == keyStart) throw Error(position, tag, "Invalid attribute name.");
                var key = input[keyStart..position].ToUpperInvariant();
                while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
                if (position >= input.Length || input[position++] != '=') throw Error(position - 1, tag, "Expected attribute=value.");
                while (position < input.Length && char.IsWhiteSpace(input[position])) position++;
                if (position >= input.Length) throw Error(offset, tag, "Missing attribute value.");
                var value = new StringBuilder();
                if (input[position] is '\'' or '"')
                {
                    var quote = input[position++]; var closed = false;
                    while (position < input.Length)
                    {
                        var ch = input[position++];
                        if (ch != quote) { value.Append(ch); continue; }
                        if (position < input.Length && input[position] == quote) { value.Append(quote); position++; continue; }
                        closed = true; break;
                    }
                    if (!closed) throw Error(offset, tag, "Unterminated attribute quote.");
                }
                else while (position < input.Length && !char.IsWhiteSpace(input[position]) && input[position] != '.') value.Append(input[position++]);
                if (!attributes.TryAdd(key, value.ToString())) throw Error(offset, key, "Duplicate attribute.");
            }
            void Attributes(params string[] names)
            {
                if (attributes.Keys.Any(k => !names.Contains(k)) || names.Any(k => !attributes.ContainsKey(k))) throw Error(offset, tag, "Expected attributes: " + string.Join(", ", names));
            }
            void InHelp() { if (module is null) throw Error(offset, tag, "Tag requires an open HELP module."); }
            if (ended) throw Error(offset, tag, "No tags may follow EPNLGRP.");
            if (!started && tag != "PNLGRP") throw Error(offset, tag, "PNLGRP must be first.");
            switch (tag)
            {
                case "PNLGRP": Attributes(); if (started) throw Error(offset, tag, "Nested panel groups are invalid."); started = true; break;
                case "EPNLGRP": Attributes(); if (module is not null) throw Error(offset, tag, "Close HELP first."); ended = true; break;
                case "HELP":
                    Attributes("NAME"); if (module is not null) throw Error(offset, tag, "Nested HELP modules are invalid.");
                    var moduleName = attributes["NAME"].ToUpperInvariant();
                    if (!ValidModule(moduleName) || definition.Modules.Any(m => m.Name == moduleName) || definition.Modules.Count == 256) throw Error(offset, tag, "Invalid, duplicate or excessive module name.");
                    module = new() { Name = moduleName }; definition.Modules.Add(module); block = null; title.Clear(); break;
                case "EHELP":
                    Attributes(); InHelp(); if (link is not null || style != "PLAIN") throw Error(offset, tag, "Close link/emphasis tags first.");
                    FinishTitle(); if (module!.Title.Length == 0) module.Title = module.Name; module = null; block = null; break;
                case "P": case "XH1": case "XH2": case "XH3":
                    Attributes(); InHelp(); if (link is not null || style != "PLAIN") throw Error(offset, tag, "Paragraphs cannot split link/emphasis tags.");
                    FinishTitle(); block = new() { Kind = tag }; module!.Blocks.Add(block); break;
                case "HP1": case "HP2": case "HP3":
                    Attributes(); InHelp(); if (block is null || style != "PLAIN") throw Error(offset, tag, "Emphasis requires paragraph text and cannot nest."); style = tag; break;
                case "EHP1": case "EHP2": case "EHP3":
                    Attributes(); if (style != tag[1..]) throw Error(offset, tag, "Mismatched emphasis tag."); style = "PLAIN"; break;
                case "ISCH":
                    Attributes("ROOTS"); InHelp();
                    var roots = attributes["ROOTS"].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (roots.Length is < 1 or > 50 || roots.Any(r => r.Length > 64 || !r.All(char.IsLetterOrDigit))) throw Error(offset, tag, "Expected 1–50 alphanumeric index words.");
                    module!.IndexWords.AddRange(roots.Select(r => r.ToUpperInvariant())); break;
                case "LINK":
                    Attributes("PERFORM"); InHelp(); if (link is not null || block is null) throw Error(offset, tag, "Links require paragraph text and cannot nest.");
                    var action = attributes["PERFORM"].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (action.Length is < 2 or > 3 || !action[0].Equals("DSPHELP", StringComparison.OrdinalIgnoreCase) || !ValidModule(action[1])) throw Error(offset, tag, "Only DSPHELP module [panel-group] links are supported.");
                    if (action.Length == 3 && !ValidGroup(action[2])) throw Error(offset, tag, "Invalid target panel group.");
                    link = new(action[1].ToUpperInvariant(), action.Length == 3 ? action[2].ToUpperInvariant() : null, ++linkId); break;
                case "ELINK": Attributes(); if (link is null) throw Error(offset, tag, "No link to close."); link = null; break;
                default: throw Error(offset, tag, "Unsupported UIM tag.");
            }
        }
        if (!started || !ended || module is not null || definition.Modules.Count == 0) throw Error(source.Length, "EPNLGRP", "Incomplete or empty panel group.");
        foreach (var target in definition.Modules.SelectMany(m => m.Blocks).SelectMany(b => b.Spans).Where(s => s.Link?.PanelGroup is null && s.Link is not null).Select(s => s.Link!))
            if (!definition.Modules.Any(m => m.Name == target.Module)) throw Error(0, "LINK", "Local help target does not exist: " + target.Module);
        return definition;
    }
    public static bool ValidGroup(string group)
    {
        var parts = group.ToUpperInvariant().Split('/'); return parts.Length is 1 or 2 && parts.All(ObjectName.IsValid);
    }
}
