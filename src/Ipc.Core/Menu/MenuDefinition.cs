using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ipc.Core.Objects;
using Ipc.Core.Security;

namespace Ipc.Core.Menu;

public static class MenuDefinition
{
    public const int MaximumBytes = 65536;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8, Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };
    public static ApplicationMenu Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw new ArgumentException("Menu JSON exceeds 64 KiB.");
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 8 });
            CheckProperties(document.RootElement);
            var menu = JsonSerializer.Deserialize<ApplicationMenu>(bytes, Json) ?? throw new ArgumentException("Missing menu definition.");
            Validate(menu); return menu;
        }
        catch (JsonException error) { throw new ArgumentException("Invalid menu JSON.", error); }
    }
    private static void CheckProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new ArgumentException("Duplicate menu JSON property."); CheckProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckProperties(child);
    }
    public static string Serialize(ApplicationMenu menu) { Validate(menu); return JsonSerializer.Serialize(menu, Json); }
    public static void Validate(ApplicationMenu menu)
    {
        if (!ObjectName.IsValid(menu.Name) || !ObjectName.IsValid(menu.Library ?? "QSYS")) throw new ArgumentException("Invalid menu identity.");
        if (menu.Title is null || menu.Title.Length > 60 || menu.Title.Any(Unsafe)) throw new ArgumentException("Menu title must fit 60 printable characters.");
        if (menu.Options is null || menu.Options.Count > 16 || menu.Options.Any(o => o is null) || menu.Options.Select(o => o.Number).Distinct().Count() != menu.Options.Count)
            throw new ArgumentException("Menu requires at most 16 distinct options.");
        foreach (var option in menu.Options)
        {
            if (!int.TryParse(option.Number, out var number) || number is < 1 or > 999 || option.Number != number.ToString(CultureInfo.InvariantCulture) ||
                string.IsNullOrWhiteSpace(option.Text) || option.Text.Length > 60 || option.Text.Any(Unsafe) || option.Target is null || option.Target.Length > 512 || option.Target.Any(Unsafe) || !Enum.IsDefined(option.Kind))
                throw new ArgumentException("Invalid menu option.");
            if (option.Kind is MenuOptionKind.SubMenu or MenuOptionKind.Command or MenuOptionKind.Prompt && string.IsNullOrWhiteSpace(option.Target)) throw new ArgumentException("Menu option requires a target.");
            if (option.Kind == MenuOptionKind.SubMenu) _ = QualifiedName.Parse(option.Target, "*LIBL");
            if (option.RequiredAuthority is null) throw new ArgumentException("Menu option authority is required; use *NONE for unrestricted options.");
            _ = SpecialAuthorities.Parse(option.RequiredAuthority);
        }
    }
    private static bool Unsafe(char c) => char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.OtherNotAssigned ||
        c is >= '\u1100' and <= '\u115f' or '\u2329' or '\u232a' or >= '\u2e80' and <= '\ua4cf' or >= '\uac00' and <= '\ud7a3' or
            >= '\uf900' and <= '\ufaff' or >= '\ufe10' and <= '\ufe19' or >= '\ufe30' and <= '\ufe6f' or >= '\uff00' and <= '\uff60' or
            >= '\uffe0' and <= '\uffe6' or >= '\u2600' and <= '\u27bf';
}
