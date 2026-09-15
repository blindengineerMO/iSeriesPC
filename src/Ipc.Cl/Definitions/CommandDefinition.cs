using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Cl.Definitions;

public sealed record ParameterDefinition
{
    public required string Keyword { get; init; }
    public string Type { get; init; } = "*CHAR";
    public int Length { get; init; } = 32;
    public int Decimals { get; init; }
    public int Minimum { get; init; }
    public int Maximum { get; init; } = 1;
    public string? Default { get; init; }
    public string Prompt { get; init; } = "";
    public int PromptOrder { get; init; }
    public string Help { get; init; } = "";
    public bool Secret { get; init; }
    public bool Literal { get; init; }
    public bool MixedCase { get; init; }
    public bool Restricted { get; init; }
    public string FileUsage { get; init; } = "*NO";
    public string? DefaultLibrary { get; init; }
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> SpecialValues { get; init; } = new Dictionary<string, string>();
    public decimal? RangeMinimum { get; init; }
    public decimal? RangeMaximum { get; init; }
    public int SourceLine { get; init; }
}

public sealed record CommandDefinition
{
    public int Version { get; init; } = 1;
    public string Title { get; init; } = "";
    public string? ProcessingProgram { get; init; }
    public string? Builtin { get; init; }
    public string? HelpPanelGroup { get; init; }
    public string? HelpId { get; init; }
    public IReadOnlyList<ParameterDefinition> Parameters { get; init; } = Array.Empty<ParameterDefinition>();
    public int MaximumPositional { get; init; } = 128;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public string ToJson() { Validate(); return JsonSerializer.Serialize(this, Json); }
    public static CommandDefinition FromJson(string value)
    {
        if (value.Length > 1048576) throw Invalid("Command metadata exceeds 1 MiB.");
        try
        {
            using var document = JsonDocument.Parse(value, new() { MaxDepth = 16 });
            CheckProperties(document.RootElement);
            var definition = JsonSerializer.Deserialize<CommandDefinition>(value, Json) ?? throw Invalid("Missing command metadata."); definition.Validate(); return definition;
        }
        catch (JsonException) { throw Invalid("Malformed command metadata."); }
    }
    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw Invalid("Duplicate command metadata property."); CheckProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckProperties(child);
    }
    public void Validate()
    {
        if (Version != 1 || Title is null || Title.Length > 256 || Title.Any(char.IsControl) || Parameters is null || Parameters.Count > 128 ||
            Parameters.Any(p => p is null) || Parameters.Select(p => p.Keyword).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Parameters.Count || MaximumPositional is < 0 or > 128)
            throw Invalid("Invalid command definition.");
        if ((Builtin is null) == (ProcessingProgram is null)) throw Invalid("Specify a processing program or a built-in target.");
        if (Builtin is not null && !ObjectName.IsValid(Builtin)) throw Invalid("Invalid built-in target.");
        if (ProcessingProgram is not null) _ = QualifiedName.Parse(ProcessingProgram);
        if (HelpPanelGroup is not null) _ = QualifiedName.Parse(HelpPanelGroup);
        if (HelpId is not null && (HelpPanelGroup is null || !ObjectName.IsValid(HelpId))) throw Invalid("Help ID requires a panel group and a valid module name.");
        foreach (var parameter in Parameters)
        {
            if (!ObjectName.IsValid(parameter.Keyword) || parameter.Length is < 1 or > 4096 || parameter.Minimum is < 0 or > 256 || parameter.Maximum < Math.Max(1, parameter.Minimum) || parameter.Maximum > 256 ||
                parameter.Decimals is < 0 or > 28 || parameter.Decimals > parameter.Length || parameter.PromptOrder is < 0 or > 999 || parameter.Prompt is null || parameter.Prompt.Length > 256 || parameter.Help is null || parameter.Help.Length > 4096 ||
                parameter.Values is null || parameter.Values.Count > 256 || parameter.SpecialValues is null || parameter.SpecialValues.Count > 256 || parameter.RangeMinimum > parameter.RangeMaximum || parameter.Prompt.Any(char.IsControl) || parameter.Help.Any(char.IsControl) ||
                parameter.Values.Any(v => v is null || v.Length > 4096 || v.Any(char.IsControl)) || parameter.SpecialValues.Any(v => v.Key.Length > 4096 || v.Value is null || v.Value.Length > 4096 || v.Key.Any(char.IsControl) || v.Value.Any(char.IsControl)) || parameter.Default?.Length > 32768)
                throw Invalid("Invalid definition for parameter " + parameter.Keyword + ".");
            if (parameter.Type is not ("*RAW" or "*CHAR" or "*NAME" or "*GENERIC" or "*PNAME" or "*DEC" or "*INT2" or "*INT4" or "*UINT2" or "*UINT4" or "*LGL" or "*DATE" or "*TIME")) throw Invalid("Unsupported parameter type " + parameter.Type + ".");
            if (parameter.FileUsage is not ("*NO" or "*IN" or "*OUT" or "*UPD" or "*INOUT" or "*UNSPFD")) throw Invalid("Invalid file usage.");
            if (parameter.MixedCase && parameter.Type is not ("*CHAR" or "*PNAME")) throw Invalid("Mixed case requires a character or path parameter.");
            if (parameter.Type == "*RAW" && Builtin is null) throw Invalid("Raw parameters are reserved for built-in command adapters.");
            if (parameter.DefaultLibrary is not null && (parameter.Type != "*NAME" || parameter.Length != 10 || parameter.DefaultLibrary is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(parameter.DefaultLibrary))) throw Invalid("Invalid qualified object parameter.");
            if (parameter.Type == "*DEC" && parameter.Length > 29) throw Invalid("Decimal parameters support at most 29 digits.");
            if (parameter.Restricted && parameter.Values.Count == 0 && parameter.SpecialValues.Count == 0) throw Invalid("Restricted parameter requires values.");
            if ((parameter.RangeMinimum is not null || parameter.RangeMaximum is not null) && parameter.Type is not ("*DEC" or "*INT2" or "*INT4" or "*UINT2" or "*UINT4")) throw Invalid("Ranges require numeric parameters.");
            if (parameter.Default is not null && parameter.Minimum != 0) throw Invalid("A required parameter cannot have a default.");
            if (parameter.Default is not null) _ = CommandBinder.Values(parameter, parameter.Default);
        }
    }
    public CommandMetadata PromptMetadata(string name) => new(name, Parameters.Select((p, index) => (Parameter: p, Index: index))
        .OrderBy(pair => pair.Parameter.PromptOrder == 0 ? 1000 + pair.Index : pair.Parameter.PromptOrder)
        .Select(pair => { var p = pair.Parameter; return new CommandParameter(p.Keyword, p.Prompt.Length == 0 ? p.Keyword : p.Prompt,
            p.Literal || p.Maximum == 1 && p.Type is "*CHAR" or "*PNAME", p.Secret, Math.Min(4096, Math.Max(p.Length, p.Maximum * (p.Length + 3) + 21)), pair.Index, p.Default); }).ToArray(), MaximumPositional, HelpPanelGroup is null ? null : new Ipc.Core.Menu.HelpRequest(HelpPanelGroup, HelpId ?? "GENERAL"), Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ToJson()))));
    public IReadOnlyList<string> Describe(string name) => new[] { name + " - " + Title, "Processing program: " + (ProcessingProgram ?? "built-in " + Builtin), "Accepted parameters: " + string.Join(", ", Parameters.Select(p => p.Keyword)) }
        .Concat(Parameters.Select(p => $"{p.Keyword}: {p.Prompt}; {p.Type}({p.Length},{p.Decimals}), {p.Minimum}..{p.Maximum} value(s)" + (p.Default is null ? "" : "; default " + (p.Secret ? "[hidden]" : p.Default)) + (p.Values.Count == 0 ? "" : "; choices " + (p.Secret ? "[hidden]" : string.Join(' ', p.Values))) + (p.Help.Length == 0 ? "" : "; " + p.Help))).ToArray();
    internal static CpfException Invalid(string text) => new("IPC0136", text);
}

public sealed record BoundCommand(CommandCall Call, IReadOnlyList<object?> Arguments);

public static class CommandBinder
{
    public static BoundCommand Bind(CommandDefinition definition, CommandCall call)
    {
        if (call.ToString().Length > 32768) throw CommandDefinition.Invalid("Command exceeds 32768 characters.");
        definition.Validate(); var supplied = call.Keywords.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        if (call.Positional.Count > Math.Min(definition.MaximumPositional, definition.Parameters.Count)) throw CommandDefinition.Invalid("Too many positional parameters.");
        for (var i = 0; i < call.Positional.Count; i++) if (!supplied.TryAdd(definition.Parameters[i].Keyword, call.Positional[i])) throw CommandDefinition.Invalid("Parameter specified by both position and keyword: " + definition.Parameters[i].Keyword);
        foreach (var keyword in supplied.Keys) if (!definition.Parameters.Any(p => p.Keyword.Equals(keyword, StringComparison.OrdinalIgnoreCase))) throw CommandDefinition.Invalid("Unknown parameter " + keyword + ".");
        var canonical = new Dictionary<string, string>(); var arguments = new List<object?>();
        foreach (var parameter in definition.Parameters)
        {
            var present = supplied.TryGetValue(parameter.Keyword, out var raw); raw ??= parameter.Default;
            if (raw is null)
            {
                if (parameter.Minimum > 0) throw CommandDefinition.Invalid("Required parameter " + parameter.Keyword + " is missing.");
                arguments.Add(parameter.Maximum > 1 ? Array.Empty<object?>() : parameter.Type is "*DEC" or "*INT2" or "*INT4" or "*UINT2" or "*UINT4" ? 0m : parameter.Type == "*LGL" ? false : "");
                continue;
            }
            var values = Values(parameter, raw); arguments.Add(parameter.Maximum == 1 ? values.Single() : values);
            canonical[parameter.Keyword] = parameter.Type == "*RAW" ? raw : string.Join(' ', values.Select(Format));
        }
        return new(new CommandCall { Name = call.Name, Keywords = canonical }, arguments);
    }
    public static IReadOnlyList<object?> Values(ParameterDefinition parameter, string raw)
    {
        // A positional list is enclosed in parentheses; a keyword list already had its
        // outer parentheses removed by CommandParser. Both use the same binder.
        if (parameter.Maximum > 1 && raw.StartsWith('(') && raw.EndsWith(')') && CommandParser.Tokenize(raw).Count == 1) raw = raw[1..^1];
        var tokens = parameter.Maximum == 1 ? new[] { raw } : CommandParser.Tokenize(raw).ToArray();
        if (tokens.Length < parameter.Minimum || tokens.Length > parameter.Maximum || parameter.Maximum == 1 && tokens.Length != 1) throw CommandDefinition.Invalid("Wrong value count for " + parameter.Keyword + ".");
        try { return tokens.Select(token => Scalar(parameter, token)).ToArray(); }
        catch (Exception error) when (error is ArgumentException or FormatException or ClParseException) { throw CommandDefinition.Invalid(parameter.Keyword + ": invalid value."); }
    }
    private static object? Scalar(ParameterDefinition p, string raw, bool special = false)
    {
        CpfException Error(string reason) => CommandDefinition.Invalid(p.Keyword + ": " + reason);
        if (p.Type == "*RAW") return raw;
        var tokens = CommandParser.Tokenize(raw);
        if (tokens.Count != 1) throw Error("one value is required; quote text containing spaces.");
        var quoted = raw.StartsWith('\'') && raw.EndsWith('\'');
        var text = CommandParser.Unquote(raw);
        if (!p.MixedCase && !quoted) text = text.ToUpperInvariant();
        if (text.Any(char.IsControl)) throw Error("control characters are not allowed.");
        if (p.SpecialValues.TryGetValue(text, out var mapped)) return Scalar(p with { SpecialValues = new Dictionary<string, string>(), Restricted = false }, mapped, special: true);
        if (p.Restricted && !p.Values.Contains(text, StringComparer.Ordinal)) throw Error("value is not one of the permitted choices.");
        if (p.Type is "*CHAR" or "*NAME" or "*GENERIC" or "*PNAME")
        {
            if (special && text.StartsWith('*')) return text;
            if (p.DefaultLibrary is not null)
            {
                var name = QualifiedName.Parse(text, p.DefaultLibrary ?? "*LIBL"); text = name.ToString();
            }
            else if (p.Type is "*NAME" or "*GENERIC")
            {
                var name = p.Type == "*GENERIC" && text.EndsWith('*') ? text[..^1] : text;
                if (!ObjectName.IsValid(name)) throw Error("invalid object name.");
            }
            if (text.Length > (p.DefaultLibrary is not null ? 21 : p.Length)) throw Error("value is too long.");
            return text;
        }
        if (p.Type == "*LGL") return text switch { "0" or "*NO" or "*FALSE" => false, "1" or "*YES" or "*TRUE" => true, _ => throw Error("logical value must be 0 or 1.") };
        if (p.Type == "*DATE")
        {
            DateOnly date;
            var native = text.Length == 7 && text[0] is '0' or '1';
            if (native) text = (text[0] == '0' ? "19" : "20") + text[1..];
            if (!DateOnly.TryParseExact(text, native ? new[] { "yyyyMMdd" } : new[] { "yyyy-MM-dd", "MMddyyyy", "MMddyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) || date.Year is < 1900 or > 2099) throw Error("invalid date (1900–2099).");
            return (date.Year >= 2000 ? "1" : "0") + date.ToString("yyMMdd", CultureInfo.InvariantCulture);
        }
        if (p.Type == "*TIME") return TimeOnly.TryParseExact(text, new[] { "HH:mm:ss", "HHmmss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToString("HHmmss", CultureInfo.InvariantCulture) : throw Error("time must be HH:mm:ss.");
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) throw Error("invalid numeric value.");
        if (p.Type == "*DEC")
        {
            var pieces = text.TrimStart('+', '-').Split('.'); var whole = pieces[0].TrimStart('0').Length; var fraction = pieces.Length > 1 ? pieces[1].Length : 0;
            if (whole > p.Length - p.Decimals || fraction > p.Decimals) throw Error("numeric precision or scale exceeded.");
        }
        else if (number != decimal.Truncate(number) || p.Type switch { "*INT2" => number < short.MinValue || number > short.MaxValue, "*INT4" => number < int.MinValue || number > int.MaxValue, "*UINT2" => number < 0 || number > ushort.MaxValue, "*UINT4" => number < 0 || number > uint.MaxValue, _ => true }) throw Error("integer is out of range.");
        if (p.RangeMinimum is { } min && number < min || p.RangeMaximum is { } max && number > max) throw Error("value is outside the allowed range.");
        return number;
    }
    public static string Format(object? value) => value switch { bool flag => flag ? "1" : "0", decimal number => number.ToString(CultureInfo.InvariantCulture), string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'", null => "''", _ => throw CommandDefinition.Invalid("Unsupported command value.") };
}
