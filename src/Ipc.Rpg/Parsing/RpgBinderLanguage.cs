using System.Security.Cryptography;
using System.Text;
using Ipc.Core.Compilation;
using Ipc.Core.Text;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    public static RpgExportLevel GeneratedExports(IReadOnlyList<string> symbols)
    {
        var level = new RpgExportLevel("*CURRENT", new string('0', 32), symbols.ToArray()); level.Validate();
        return level with { Signature = GeneratedSignature(symbols) };
    }
    private static string GeneratedSignature(IReadOnlyList<string> symbols) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', symbols))).AsSpan(0, 16));
    public static IReadOnlyList<RpgExportLevel> ParseBinderSource(SourceDocument source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(source.Identity) || source.Identity.Length > 1024 || source.Identity.Any(char.IsControl)) throw new RpgCompileException(0, "Invalid binder source identity.");
        if (source.Text is null || source.Text.Length > 1048576 || Encoding.UTF8.GetByteCount(source.Text) > 1048576) throw new RpgCompileException(0, "Binder source exceeds 1 MiB.");
        var lines = source.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var levels = new List<RpgExportLevel>(); var symbols = new List<string>(); string? level = null, signature = null;
        var text = new StringBuilder(); var firstLine = 0; var comment = false; var quoted = false;
        RpgCompileException Error(int line, string message) => new(new SourceLocation(source.Identity, line, 1, []), message);
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var clean = new StringBuilder();
            for (var at = 0; at < lines[index].Length; at++)
            {
                var value = lines[index][at];
                if (comment) { if (value == '*' && at + 1 < lines[index].Length && lines[index][at + 1] == '/') { comment = false; at++; clean.Append(' '); } continue; }
                if (!quoted && value == '/' && at + 1 < lines[index].Length && lines[index][at + 1] == '*') { comment = true; at++; clean.Append(' '); continue; }
                clean.Append(value);
                if (value == '\'') { if (quoted && at + 1 < lines[index].Length && lines[index][at + 1] == '\'') clean.Append(lines[index][++at]); else quoted = !quoted; }
            }
            if (quoted) throw Error(index + 1, "Binder literals cannot cross source records.");
            var part = clean.ToString().Trim(); if (part.Length == 0) continue;
            if (text.Length == 0) firstLine = index + 1;
            var continued = part.EndsWith('+') || part.EndsWith('-');
            text.Append(part[..(continued ? part.Length - 1 : part.Length)]).Append(' ');
            if (continued) continue;
            var command = text.ToString().Trim(); if (command.EndsWith(';')) command = command[..^1].Trim(); text.Clear(); var split = command.IndexOfAny([' ', '\t']);
            var operation = (split < 0 ? command : command[..split]).ToUpperInvariant();
            IReadOnlyList<DeclarationKeyword> options;
            try { options = DeclarationKeywords(split < 0 ? "" : command[(split + 1)..], firstLine); }
            catch (RpgCompileException error) { throw Error(firstLine, error.Message); }
            if (options.GroupBy(o => o.Name).Any(g => g.Count() != 1)) throw Error(firstLine, "Duplicate binder keyword.");
            string? Value(string name) => options.SingleOrDefault(o => o.Name == name)?.Arguments;
            string Symbol(string value)
            {
                var name = value.StartsWith('\'') ? Literal(value, firstLine) : value.ToUpperInvariant();
                if (!RpgExportLevel.SymbolName(name)) throw Error(firstLine, "Invalid binder procedure symbol."); return name;
            }
            string Literal(string value, int line)
            {
                if (value.Length < 2 || value[0] != '\'' || value[^1] != '\'') throw Error(line, "A binder literal must be quoted.");
                var decoded = value[1..^1].Replace("''", "'", StringComparison.Ordinal);
                if (decoded.Contains('\'') && value[1..^1].Replace("''", "", StringComparison.Ordinal).Contains('\'')) throw Error(line, "Invalid binder literal.");
                return decoded;
            }
            switch (operation)
            {
                case "STRPGMEXP":
                    if (level is not null || levels.Count >= 16 || options.Any(o => o.Name is not ("PGMLVL" or "SIGNATURE" or "LVLCHK")) ||
                        options.Any(o => o.Arguments is null) || Value("LVLCHK")?.ToUpperInvariant() is not (null or "*YES")) throw Error(firstLine, "Invalid or unsupported export block options.");
                    level = (Value("PGMLVL") ?? "*CURRENT").ToUpperInvariant();
                    if (level is not ("*CURRENT" or "*PRV") || levels.Count == 0 && level != "*CURRENT" || levels.Count > 0 && level != "*PRV") throw Error(firstLine, "Binder source requires one CURRENT block followed by PRV blocks.");
                    signature = Value("SIGNATURE") ?? "*GEN"; symbols.Clear(); break;
                case "EXPORT":
                    if (level is null || options.Count != 1 || options[0] is not { Name: "SYMBOL", Arguments: { } symbol }) throw Error(firstLine, "EXPORT requires SYMBOL within an export block.");
                    var name = Symbol(symbol); if (symbols.Count >= 256 || symbols.Contains(name, StringComparer.Ordinal)) throw Error(firstLine, "Duplicate symbol or too many exports."); symbols.Add(name); break;
                case "ENDPGMEXP":
                    if (level is null || options.Count != 0 || symbols.Count == 0) throw Error(firstLine, "ENDPGMEXP requires a nonempty export block and no options.");
                    string bytes;
                    if (signature!.Equals("*GEN", StringComparison.OrdinalIgnoreCase)) bytes = GeneratedSignature(symbols);
                    else if (signature.StartsWith("X'", StringComparison.OrdinalIgnoreCase) && signature.EndsWith('\''))
                    {
                        var hex = signature[2..^1]; if (hex.Length != 32 || hex.Any(c => !Uri.IsHexDigit(c))) throw Error(firstLine, "Hex signatures require exactly 16 bytes."); bytes = hex.ToUpperInvariant();
                    }
                    else
                    {
                        var literal = Literal(signature, firstLine); var encoding = (Encoding)CodePage.FromCcsid(37).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
                        try { var encoded = encoding.GetBytes(literal); if (encoded.Length > 16 || encoded.Length == 0) throw Error(firstLine, "Signatures require 1 through 16 EBCDIC bytes."); bytes = Convert.ToHexString(encoding.GetBytes(literal.PadRight(16))); }
                        catch (EncoderFallbackException) { throw Error(firstLine, "Signature is not representable in EBCDIC 37."); }
                    }
                    if (levels.Any(l => l.Signature == bytes)) throw Error(firstLine, "Duplicate binder signature.");
                    if (levels.Count > 0 && !levels[0].Exports.Take(symbols.Count).SequenceEqual(symbols, StringComparer.Ordinal)) throw Error(firstLine, "Previous exports must preserve the current export prefix.");
                    levels.Add(new(level, bytes, symbols.ToArray())); level = null; signature = null; symbols.Clear(); break;
                default: throw Error(firstLine, "Unsupported binder operation: " + operation);
            }
        }
        if (comment || text.Length > 0 || level is not null || levels.Count == 0) throw Error(lines.Length, "Unclosed or empty binder source.");
        return levels;
    }
}
