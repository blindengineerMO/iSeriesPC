using System.Text;
using System.Text.RegularExpressions;

namespace Ipc.Core.Compilation;

public sealed record SourceDocument(string Identity, string Text);
public sealed record SourceLocation(string Source, int Line, int Column, IReadOnlyList<string> IncludeStack)
{
    public override string ToString() => $"{Source}:{Line}:{Column}" + (IncludeStack.Count == 0 ? "" : " (included from " + string.Join(" -> ", IncludeStack) + ")");
}
public sealed record PreprocessedSource(string Text, IReadOnlyList<SourceLocation> Locations, IReadOnlyDictionary<string, string> Sources);
public sealed class SourcePreprocessException(SourceLocation location, string message) : Exception($"IPC0006: {location}: {message}")
{
    public SourceLocation Location { get; } = location;
}

/// <summary>CL's explicitly defined inclusion extension. Resolution and authority belong to the caller.</summary>
public sealed class SourcePreprocessor(Func<SourceDocument, string, SourceDocument>? resolve = null)
{
    public PreprocessedSource Expand(SourceDocument root, CancellationToken cancellationToken = default)
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var activeSources = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>(); var locations = new List<SourceLocation>();
        var inputBytes = 0; var outputBytes = 0; var mapSize = 0;
        var origin = new SourceLocation(root.Identity, 1, 1, Array.Empty<string>());
        void Visit(SourceDocument requested, IReadOnlyList<string> stack, SourceLocation requestedAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stack.Count >= 16) throw new SourcePreprocessException(requestedAt, "Include depth exceeds 16 documents.");
            if (string.IsNullOrWhiteSpace(requested.Identity) || requested.Identity.Length > 1024 || requested.Identity.Any(char.IsControl))
                throw new SourcePreprocessException(requestedAt, "Invalid source identity.");
            if (!activeSources.Add(requested.Identity)) throw new SourcePreprocessException(requestedAt, "Include cycle involving " + requested.Identity + ".");
            if (!sources.TryGetValue(requested.Identity, out var source))
            {
                if (requested.Text is null || requested.Text.Length > 1048576 || requested.Text.Contains('\0') || Encoding.UTF8.GetByteCount(requested.Text) > 1048576)
                    throw new SourcePreprocessException(requestedAt, "Source exceeds 1 MiB or contains a null character.");
                source = requested.Text.Replace("\r\n", "\n", StringComparison.Ordinal);
                inputBytes += Encoding.UTF8.GetByteCount(source);
                if (sources.Count >= 64 || inputBytes > 4194304) throw new SourcePreprocessException(requestedAt, "Compilation exceeds 64 documents or 4 MiB of source.");
                sources.Add(requested.Identity, source);
            }
            var document = new SourceDocument(requested.Identity, source);
            var lines = source.Split('\n');
            if (lines.Length > 10000) throw new SourcePreprocessException(requestedAt, "Source exceeds 10000 lines.");
            var conditions = new List<Conditional>(); var enabled = true; var comment = false; var quote = false; SourceLocation? commentStart = null;
            for (var index = 0; index < lines.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var original = lines[index]; var startedInQuote = quote; var startedInComment = comment;
                var lineComment = !comment && !quote && (original.TrimStart().StartsWith('*') || original.TrimStart().StartsWith("//", StringComparison.Ordinal));
                var raw = lineComment ? original : StripComments(original, ref comment, ref quote); var text = raw.TrimStart();
                var location = new SourceLocation(document.Identity, index + 1, raw.Length - text.Length + 1, stack);
                if (!startedInComment && comment) commentStart = location;
                SourcePreprocessException Error(string message) => new(location, message);
                if (raw.Length > 8192) throw Error("Source line exceeds 8192 characters.");
                if (startedInQuote || !text.StartsWith('/') || text.StartsWith("//", StringComparison.Ordinal))
                {
                    if (!enabled) continue;
                    outputBytes += Encoding.UTF8.GetByteCount(raw) + 1;
                    mapSize += location.Source.Length + stack.Sum(s => s.Length) + 48;
                    if (output.Count >= 50000 || outputBytes > 4194304 || mapSize > 4194304) throw Error("Expanded source or location map exceeds its bounded size.");
                    output.Add(raw); locations.Add(location); continue;
                }
                var separator = text.IndexOfAny(new[] { ' ', '\t' });
                var directive = (separator < 0 ? text : text[..separator]).ToUpperInvariant();
                var argument = separator < 0 ? "" : text[(separator + 1)..].Trim();
                switch (directive)
                {
                    case "/INCLUDE":
                        var reference = IncludeReference(argument, Error);
                        if (!enabled) break;
                        if (resolve is null) throw Error("CL /INCLUDE requires a source resolver.");
                        SourceDocument included;
                        try { included = resolve(document, reference); }
                        catch (Ipc.Core.Messages.CpfException failure)
                        { throw new Ipc.Core.Messages.CpfException(failure.MessageId, $"{location}: {failure.Message}"); }
                        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
                        { throw Error("Cannot resolve include " + reference + ": " + failure.Message); }
                        Visit(included ?? throw Error("Include resolver returned no source."), stack.Append($"{location.Source}:{location.Line}:{location.Column}").ToArray(), location);
                        break;
                    case "/DEFINE":
                        if (!Symbol(argument)) throw Error("/DEFINE requires one symbol (1–64 letters, digits or underscores, starting with a letter).");
                        if (enabled && symbols.Add(argument) && symbols.Count > 256) throw Error("Compilation exceeds 256 defined symbols.");
                        break;
                    case "/IF":
                        var result = Condition(argument, symbols, enabled, Error);
                        if (conditions.Count >= 64) throw Error("Conditional nesting exceeds 64.");
                        conditions.Add(new(enabled, result, false, location)); enabled = enabled && result; break;
                    case "/ELSE":
                        if (argument.Length != 0 || conditions.Count == 0 || conditions[^1].ElseSeen) throw Error("Unmatched, repeated or malformed /ELSE.");
                        var condition = conditions[^1]; conditions[^1] = condition with { ElseSeen = true };
                        enabled = condition.ParentEnabled && !condition.Result; break;
                    case "/ENDIF":
                        if (argument.Length != 0 || conditions.Count == 0) throw Error("Unmatched or malformed /ENDIF.");
                        enabled = conditions[^1].ParentEnabled; conditions.RemoveAt(conditions.Count - 1); break;
                    default:
                        throw Error("Unsupported CL preprocessing directive " + directive + "; RPG directives are not native CL syntax.");
                }
            }
            if (conditions.Count != 0) throw new SourcePreprocessException(conditions[^1].Location, "Unclosed /IF; each source member must balance its own conditionals.");
            if (comment) throw new SourcePreprocessException(commentStart ?? requestedAt, "Unclosed CL block comment.");
            activeSources.Remove(document.Identity);
        }
        Visit(root, Array.Empty<string>(), origin);
        return new(string.Join('\n', output), locations, sources);
    }
    private sealed record Conditional(bool ParentEnabled, bool Result, bool ElseSeen, SourceLocation Location);
    private static string StripComments(string line, ref bool comment, ref bool quote)
    {
        var result = line.ToCharArray();
        for (var index = 0; index < line.Length; index++)
        {
            if (comment)
            {
                result[index] = ' ';
                if (line[index] == '*' && index + 1 < line.Length && line[index + 1] == '/') { result[++index] = ' '; comment = false; }
            }
            else if (line[index] == '\'')
            {
                if (quote && index + 1 < line.Length && line[index + 1] == '\'') index++;
                else quote = !quote;
            }
            else if (!quote && line[index] == '/' && index + 1 < line.Length)
            {
                if (line[index + 1] == '*') { result[index] = ' '; result[++index] = ' '; comment = true; }
                else if (line[index + 1] == '/') break; // Existing CL line-comment extension.
            }
        }
        return new string(result);
    }
    private static bool Symbol(string name) => Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);
    private static bool Condition(string text, HashSet<string> symbols, bool active, Func<string, SourcePreprocessException> error)
    {
        var match = Regex.Match(text, @"\A(?:(NOT|\*NOT)\s+)?DEFINED\(\s*([A-Za-z][A-Za-z0-9_]{0,63})\s*\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (match.Success) return match.Groups[1].Success != symbols.Contains(match.Groups[2].Value);
        if (!Symbol(text)) throw error("/IF requires DEFINED(symbol), NOT DEFINED(symbol), or a defined symbol.");
        if (active && !symbols.Contains(text)) throw error("Undefined conditional symbol " + text + ".");
        return symbols.Contains(text);
    }
    private static string IncludeReference(string text, Func<string, SourcePreprocessException> error)
    {
        if (text.Length is < 1 or > 1024) throw error("/INCLUDE requires one bounded member or path reference.");
        if (text.StartsWith('\''))
        {
            var value = new StringBuilder();
            for (var index = 1; index < text.Length; index++)
            {
                if (text[index] != '\'') { value.Append(text[index]); continue; }
                if (index == text.Length - 1 && value.Length > 0) return value.ToString();
                if (index + 1 < text.Length && text[index + 1] == '\'') { value.Append('\''); index++; continue; }
                throw error("Invalid quoted include reference.");
            }
            throw error("Unclosed or empty include reference.");
        }
        if (text.Any(char.IsWhiteSpace) || text.Contains('\'')) throw error("Include paths containing spaces must be quoted.");
        return text;
    }
}
