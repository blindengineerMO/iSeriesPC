using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static Func<RpgExternalRequest, RpgExternalLayout>? CachedExternalResolver(Func<RpgExternalRequest, RpgExternalLayout>? resolver)
    {
        if (resolver is null) return null;
        var cache = new Dictionary<RpgExternalRequest, RpgExternalLayout>();
        var canonical = new Dictionary<(string, string), RpgExternalLayout>();
        return request =>
        {
            if (cache.TryGetValue(request, out var layout)) return layout;
            if (cache.Count >= 256) throw new RpgCompileException(0, "At most 256 external structure descriptions are supported.");
            layout = resolver(request); RpgExternalBindings.Validate(new(request, layout));
            var key = (layout.File, layout.Format);
            if (canonical.TryGetValue(key, out var existing))
            {
                if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(layout)) throw new InvalidDataException("Conflicting external structure description snapshots.");
                layout = existing;
            }
            else canonical.Add(key, layout);
            cache.Add(request, layout); return layout;
        };
    }
    private static string ExternalNameValue(RpgProgram program, string text, int line)
    {
        if (DeclarationValue(program, text, line) is not string name) throw new RpgCompileException(line, "External names require character literals or earlier named constants.");
        return name;
    }
    private static IReadOnlyList<RpgExternalField> ExternalFields(RpgExternalLayout layout, string extract) => extract == "*KEY"
        ? layout.Fields.Where(f => f.KeySequence > 0).OrderBy(f => f.KeySequence).ToArray()
        : layout.Fields.Where(f => extract == "*ALL" || extract == "*INPUT" && f.Input || extract == "*OUTPUT" && f.Output).ToArray();
    private static RpgDataStructure ExternalStructureTemplate(RpgProgram program, string name, IReadOnlyList<DeclarationKeyword> options, int line)
    {
        var ext = options.SingleOrDefault(k => k.Name == "EXT"); var extname = options.SingleOrDefault(k => k.Name == "EXTNAME");
        if (ext is not null && (options[0] != ext || ext.Arguments is not null) || options.Any(k => k.Name is "LIKEDS" or "LIKEREC" or "PSDS"))
            throw new RpgCompileException(line, "EXT must be first and argument-free; external structures cannot combine LIKEDS, LIKEREC or PSDS.");
        var parts = extname is null ? Array.Empty<string>() : DeclarationArguments(extname.Arguments ?? "", line);
        if (extname is not null && (parts.Length is < 1 or > 4 || parts.Any(p => p.Length == 0))) throw new RpgCompileException(line, "EXTNAME requires a file, optional format and optional extract type.");
        var file = extname is null ? name : ExternalNameValue(program, parts[0], line);
        var format = parts.Length > 1 && !parts[1].StartsWith('*') ? ExternalNameValue(program, parts[1], line) : null;
        var (extract, nullMap) = NullExtractTypes(parts.Skip(format is null ? 1 : 2).ToArray(), line);
        var explicitExtract = parts.Skip(format is null ? 1 : 2).Any();
        if (!RpgWorkstationBindings.FileName(file) || format is not null && !Ipc.Core.Objects.ObjectName.IsValid(format))
            throw new RpgCompileException(line, "EXTNAME requires valid external file and format names.");
        RpgExternalLayout layout;
        try { layout = program.ExternalDescriptionLookup?.Invoke(new(file, format)) ?? throw new RpgCompileException(line, "EXTNAME requires a compile-time external description resolver."); }
        catch (RpgCompileException error) when (error.LineNumber == 0) { throw new RpgCompileException(line, error.Message); }
        catch (Exception error) when (error is InvalidDataException or Ipc.Core.Messages.CpfException) { throw new RpgCompileException(line, error.Message); }
        var fields = ExternalFields(layout, extract);
        if (fields.Count == 0) throw new RpgCompileException(line, "The external extract has no supported fields.");
        var prefixParts = options.SingleOrDefault(k => k.Name == "PREFIX") is { } prefix ? DeclarationArguments(prefix.Arguments ?? "", line) : [];
        if (prefixParts.Length > 2 || options.Any(k => k.Name == "PREFIX") && prefixParts.Length == 0) throw new RpgCompileException(line, "PREFIX requires a prefix and optional replacement count.");
        var prefixText = prefixParts.Length == 0 ? "" : prefixParts[0].StartsWith('\'') ? ExternalNameValue(program, prefixParts[0], line) : prefixParts[0].ToUpperInvariant();
        prefixText = prefixText.ToUpperInvariant();
        var replace = prefixParts.Length == 2 ? DeclarationInteger(program, prefixParts[1], line, 0, 128) : 0;
        var init = options.SingleOrDefault(k => k.Name == "INZ");
        if (init?.Arguments is not null && !init.Arguments.Equals("*EXTDFT", StringComparison.OrdinalIgnoreCase)) throw new RpgCompileException(line, "External structure INZ is argument-free or INZ(*EXTDFT).");
        if (nullMap && init?.Arguments is not null) throw new RpgCompileException(line, "INZ(*EXTDFT) null-map defaults require a remaining external default contract.");
        var signature = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(layout)));
        var ds = new RpgDataStructure { Name = "\u0001EXTERNAL", NativeStorage = true, Qualified = true, TypeOrigin = layout,
            RecordOrigin = new("EXTNAME", layout.Format, signature, extract, false, "EXTNAME:" + layout.File + ":" + layout.Format, new(layout, explicitExtract), nullMap) };
        var extent = 0;
        foreach (var field in fields)
        {
            if (replace > field.Name.Length) throw new RpgCompileException(line, "PREFIX replacement exceeds an external field name.");
            var local = prefixText + field.Name[replace..];
            if (!RpgWorkstationBindings.Name(local) || ds.Find(local) is not null) throw new RpgCompileException(line, "Invalid or duplicate external subfield name after PREFIX.");
            var offset = nullMap ? extract == "*KEY" || layout.Device == "WORKSTN" ? extent : layout.Fields.ToList().IndexOf(field) : extract == "*KEY" || layout.Device == "WORKSTN" ? extent : field.Position - 1;
            var initial = init?.Arguments is null || field.DefaultValue is null && !field.CurrentDatetimeDefault ? null : ExternalDefault(field, line);
            if (initial is not null && !field.CurrentDatetimeDefault) ValidateDeclarationInitializer(DeclarationValue(program, initial, line), field.Kind, field.Length, field.Decimals, false, line);
            ds.Elements.Add(new() { Name = local, LocalName = local, ExternalName = field.Name, Kind = nullMap ? RpgFieldKind.Indicator : field.Kind, Length = nullMap ? 1 : field.Length,
                Decimals = nullMap ? 0 : field.Decimals, StorageLength = nullMap ? 1 : field.StorageLength, Stride = nullMap ? 1 : field.StorageLength, Offset = offset,
                HasExplicitOffset = true, LikeType = field, NullCapable = !nullMap && field.NullCapable && program.NullControl == "*USRCTL", InitialValue = initial, HasInitialValue = initial is not null, ExternalDefaultInitialization = init?.Arguments is not null });
            extent = Math.Max(extent, offset + (nullMap ? 1 : field.StorageLength));
        }
        ds.Length = extract == "*KEY" || layout.Device == "WORKSTN" ? extent : nullMap ? layout.Fields.Count : layout.Length; return ds;
    }
    private static string ExternalDefault(RpgExternalField field, int line) => field.CurrentDatetimeDefault
        ? field.Kind switch { RpgFieldKind.Date => "%date()", RpgFieldKind.Time => "%time()", _ => "%timestamp()" }
        : field.Kind switch
    {
        RpgFieldKind.Character => DeclarationLiteral(field.DefaultValue ?? ""),
        RpgFieldKind.Date => DeclarationLiteral(field.DefaultValue ?? "0001-01-01"),
        RpgFieldKind.Time => DeclarationLiteral(field.DefaultValue ?? "00:00:00"),
        RpgFieldKind.Timestamp => DeclarationLiteral(field.DefaultValue ?? "0001-01-01T00:00:00Z"),
        RpgFieldKind.Float when double.TryParse(field.DefaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var floating) && double.IsFinite(floating) => DeclarationLiteral(floating),
        RpgFieldKind.Indicator when field.DefaultValue?.ToUpperInvariant() is "1" or "*ON" or "TRUE" => "*ON",
        RpgFieldKind.Indicator when field.DefaultValue?.ToUpperInvariant() is "0" or "*OFF" or "FALSE" => "*OFF",
        _ when decimal.TryParse(field.DefaultValue, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => DeclarationLiteral(number),
        _ => throw new RpgCompileException(line, "Unsupported external default value.")
    };
    private static string ExternalInitializer(object value, RpgFieldKind kind)
    {
        if (value is DateTimeOffset offset) value = offset.DateTime;
        if (value is DateTime date) return kind == RpgFieldKind.Date ? "%date('" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "')"
            : "%timestamp('" + date.ToString("yyyy-MM-dd-HH.mm.ss.ffffff", CultureInfo.InvariantCulture) + "')";
        if (value is TimeSpan time) return "%time('" + time.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture) + "')";
        return DeclarationLiteral(value);
    }
    private static void OverrideExternalSubfield(RpgProgram program, RpgDataStructure ds, string local,
        IReadOnlyList<DeclarationKeyword> keywords, HashSet<string> overrides, int line)
    {
        if (keywords.Skip(1).Any(k => k.Name is not ("INZ" or "DIM")) || local == "*N") throw new RpgCompileException(line, "EXTFLD supports a named external subfield, INZ and DIM.");
        var external = keywords[0].Arguments is null ? local : ExternalNameValue(program, keywords[0].Arguments!, line);
        var original = ds.Elements.SingleOrDefault(e => e.ExternalName == external) ?? throw new RpgCompileException(line, "EXTFLD references an unavailable external field: " + external + ".");
        var exported = ds.Qualified ? ds.Name + "." + local : local;
        if (!overrides.Add(external) || ds.Elements.Any(e => e != original && e.LocalName == local) || HasLocalDeclaration(program, exported))
            throw new RpgCompileException(line, "Duplicate external override or subfield name.");
        var count = NativeDimension(program, keywords.SingleOrDefault(k => k.Name == "DIM"), line, current: ds);
        if (original.StorageLength % count != 0) throw new RpgCompileException(line, "EXTFLD DIM must divide the external field's byte width.");
        if (count != 1 && original.NullCapable) throw new RpgCompileException(line, "Dividing nullable external fields requires a remaining null-map layout contract.");
        var width = original.StorageLength / count;
        var length = original.Kind switch { RpgFieldKind.Integer => width switch { 1 => 3, 2 => 5, 4 => 10, 8 => 20, _ => 0 }, RpgFieldKind.Packed => width * 2 - 1, _ => width };
        if (length < 1 || original.Decimals > length || original.Kind == RpgFieldKind.Float && width is not (4 or 8) || original.Kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp && count != 1)
            throw new RpgCompileException(line, "EXTFLD DIM produces an unsupported native element representation.");
        var init = keywords.SingleOrDefault(k => k.Name == "INZ"); var initial = original.InitialValue; var hasInitial = original.HasInitialValue;
        if (init is not null)
        {
            hasInitial = true;
            initial = init.Arguments?.Equals("*EXTDFT", StringComparison.OrdinalIgnoreCase) == true && original.LikeType is RpgExternalField field
                ? field.DefaultValue is null && !field.CurrentDatetimeDefault ? null : ExternalDefault(field, line) : init.Arguments;
        }
        var externalDefault = init is null ? original.ExternalDefaultInitialization : init.Arguments?.Equals("*EXTDFT", StringComparison.OrdinalIgnoreCase) == true;
        if (initial is not null && !externalDefault) { var value = DeclarationValue(program, initial, line, ds); ValidateDeclarationInitializer(value, original.Kind, length, original.Decimals, false, line); initial = ExternalInitializer(value, original.Kind); }
        ds.Elements[ds.Elements.IndexOf(original)] = new() { Name = exported, LocalName = local, ExternalName = external,
            Kind = original.Kind, Length = length, Decimals = original.Decimals, StorageLength = width, Stride = width,
            Dimension = count, IsArray = keywords.Any(k => k.Name == "DIM"), Offset = original.Offset, HasExplicitOffset = true,
            LikeType = original.LikeType, NullCapable = original.NullCapable, ExternalDefaultInitialization = externalDefault, HasInitialValue = hasInitial, InitialValue = initial, DeclarationLine = line };
    }
    private static bool ExternalDiskMatches(RpgExternalOrigin origin, RpgDiskBinding file)
        => origin.ExplicitExtract && origin.Layout.Device == "DISK" && origin.Layout.Format == file.Layout.ExternalRecord && origin.Layout.Length == file.Layout.RecordLength &&
            origin.Layout.Fields.Count == file.Layout.Fields.Count && origin.Layout.Fields.Zip(file.Layout.Fields).All(p => p.First.Name == p.Second.External &&
                p.First.Kind == p.Second.Kind && p.First.Length == p.Second.Length && p.First.Decimals == p.Second.Decimals &&
                p.First.Position == p.Second.Position && p.First.StorageLength == p.Second.StorageLength && p.First.NullCapable == p.Second.NullCapable);
    private static bool ExternalWorkstationMatches(RpgRecordOrigin origin, RpgWorkstationBinding file, RpgDisplayRecord record)
    {
        if (origin.External is not { ExplicitExtract: true, Layout.Device: "WORKSTN" } external || external.Layout.Format != record.External) return false;
        var fields = ExternalFields(external.Layout, origin.Extract); var actual = RpgWorkstationRecords.Fields(record, origin.Extract);
        return fields.Count == actual.Count && fields.Zip(actual).All(p => p.First.Name == p.Second.External && p.First.Kind == RpgWorkstationRecords.Kind(p.Second) &&
            p.First.Length == p.Second.Length && p.First.Decimals == p.Second.Decimals && p.First.StorageLength == p.Second.Length);
    }

    private static bool SharedExternalField(RpgProgram program, string name, string external, RpgFieldKind kind, int length, int decimals, bool display = false)
        => program.FindDsElement(name) is { ExternalName: not null, LikeType: RpgExternalField field } member && member.ExternalName == external &&
            field.Length == length && field.Decimals == decimals && (field.Kind == kind || display && field.Kind == RpgFieldKind.Zoned && kind == RpgFieldKind.Packed) &&
            program.DataStructures.Any(d => !d.IsTemplate && d.Elements.Contains(member));

}
