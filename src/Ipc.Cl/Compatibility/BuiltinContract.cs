using System.Text.Json;
using Ipc.Cl.Parsing;

namespace Ipc.Cl.Compatibility;

/// <summary>The release inventory is not a registry of executable handlers.</summary>
public static class BuiltinContract
{
    private static readonly JsonDocument Catalog = Load();
    private static readonly Dictionary<string, JsonElement> Commands = Catalog.RootElement
        .GetProperty("entries").EnumerateArray()
        .Where(e => e.GetProperty("kind").GetString() == "command")
        .ToDictionary(e => e.GetProperty("name").GetString()!, StringComparer.OrdinalIgnoreCase);

    private static JsonDocument Load()
    {
        using var stream = typeof(BuiltinContract).Assembly.GetManifestResourceStream("Ipc.Cl.Compatibility.catalog.json")!;
        return JsonDocument.Parse(stream);
    }

    public static IReadOnlyList<string> DefinedNames => Commands.Where(p => p.Value.TryGetProperty("keywords", out _)).Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();

    public static string CanonicalName(string name)
    {
        foreach (var alias in Catalog.RootElement.GetProperty("aliases").EnumerateObject())
            if (alias.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return alias.Value.GetString()!;
        return name.ToUpperInvariant();
    }

    public static string Unavailable(string name)
    {
        foreach (var correction in Catalog.RootElement.GetProperty("corrections").EnumerateObject())
            if (correction.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return $"IPC0004: {name} is a provisional spelling; use {correction.Value.GetString()}.";
        return Commands.ContainsKey(CanonicalName(name))
            ? $"IPC0002: Command {name} is planned but unavailable in this build."
            : $"IPC0001: Command {name} not found.";
    }

    public static IReadOnlyList<string> Describe(string name, bool available)
    {
        name = CanonicalName(name);
        if (!Commands.TryGetValue(name, out var entry)) return new[] { "Command " + name + " is not in the compatibility catalog." };
        var result = new List<string> { name + (available ? " is available in this build." : " is planned and is not available in this build.") };
        if (entry.TryGetProperty("keywords", out var keywords))
            result.Add("Accepted parameters: " + string.Join(", ", keywords.EnumerateArray().Select(k => k.GetString())) + ".");
        else result.Add("Parameter metadata is not yet published for this command.");
        result.Add("Use parameter names followed by values in parentheses. Quote values containing spaces. F3 returns without submitting the command.");
        return result;
    }

    public static Ipc.Cl.Commands.CommandMetadata? Metadata(string name)
    {
        name = CanonicalName(name);
        if (!Commands.TryGetValue(name, out var entry) || !entry.TryGetProperty("keywords", out var keywords)) return null;
        var parameters = keywords.EnumerateArray().Select(k =>
        {
            var keyword = k.GetString()!;
            var secret = keyword.Contains("PASSWORD", StringComparison.Ordinal) || keyword is "TOKEN" or "CODE" or "SECRET";
            return new Ipc.Cl.Commands.CommandParameter(keyword, keyword, keyword is "TEXT" or "MSG" or "RPY" || secret, secret);
        }).ToArray();
        return new(name, parameters, entry.GetProperty("maxPositional").GetInt32());
    }

    public static string? Validate(CommandCall call)
    {
        if (!Commands.TryGetValue(CanonicalName(call.Name), out var entry) ||
            !entry.TryGetProperty("keywords", out var keywords))
            return null;
        var allowed = keywords.EnumerateArray().Select(k => k.GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var keyword in call.Keywords.Keys)
            if (!allowed.Contains(keyword))
                return $"IPC0003: Parameter {keyword} is unsupported for {call.Name}.";
        if (call.Positional.Count > entry.GetProperty("maxPositional").GetInt32())
            return $"IPC0003: Too many positional parameters for {call.Name}; use documented keywords.";
        return null;
    }
}
