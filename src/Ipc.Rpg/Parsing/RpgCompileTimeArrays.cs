using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string ExtractCompileTimeData(RpgProgram program, PreprocessedSource source, CancellationToken cancellationToken)
    {
        var lines = source.Text.Split('\n'); var quoted = false; RpgCompileTimeData? block = null;
        var terminals = source.Sources.Where(p => p.Value.EndsWith('\n')).ToDictionary(p => p.Key, p => p.Value.Count(c => c == '\n') + 1, StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); bool? named = null;
        for (var index = 0; index < lines.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var raw = lines[index].TrimEnd('\r');
            if (!quoted && RpgSourcePreprocessor.IsDataBoundary(raw))
            {
                var header = Regex.Match(raw, @"\A\*\*CTDATA\s+([A-Za-z_][A-Za-z0-9_]*)\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var name = header.Success ? header.Groups[1].Value.ToUpperInvariant() : null;
                if (!header.Success && raw.TrimEnd() != "**") throw new RpgCompileException(index + 1, "Unsupported or malformed program-data header; use **CTDATA name or **.");
                if (name is not null && !names.Add(name)) throw new RpgCompileException(index + 1, "Duplicate compile-time data section: " + name + ".");
                if (named is { } mode && mode != (name is not null)) throw new RpgCompileException(index + 1, "Named and declaration-order compile-time data cannot be mixed.");
                named = name is not null; block = new(name, index + 1); program.CompileTimeData.Add(block);
                lines[index] = ""; continue;
            }
            if (block is not null)
            {
                var terminal = raw.Length == 0 && index < source.Locations.Count && terminals.TryGetValue(source.Locations[index].Source, out var last) && source.Locations[index].Line == last;
                if (!terminal) block.Records.Add((raw, index + 1));
                lines[index] = "";
            }
            else lines[index] = RpgSourcePreprocessor.PrepareLine(raw, ref quoted);
        }
        return string.Join('\n', lines);
    }
    private static int InferredCompileTimeCount(RpgProgram program, string name, int line)
    {
        var data = program.CompileTimeData.SingleOrDefault(d => d.Name == name);
        if (data is null || data.Records.Count is < 1 or > 32767) throw new RpgCompileException(line, "DIM(*CTDATA) requires 1 through 32767 records in a named **CTDATA section.");
        return data.Records.Count;
    }
    private static int CompileTimeWidth(RpgField field) => field.Kind == RpgFieldKind.Float ? field.Length == 4 ? 14 : 23 :
        field.Length + (field.CompileTimeFormat is "L" or "R" ? 1 : 0);

    private static void BindCompileTimeData(RpgProgram program, CancellationToken cancellationToken)
    {
        var alternates = BindAlternatingArrays(program);
        var arrays = program.Fields.Where(f => !program.GlobalFields.Contains(f) && f.CompileTimeArray && f.AlternateOf is null).ToArray(); var anonymous = 0; var fixedSeen = false;
        foreach (var block in program.CompileTimeData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var field = block.Name is { } name ? arrays.SingleOrDefault(f => f.Name == name) : anonymous < arrays.Length ? arrays[anonymous++] : null;
            if (field is null) throw new RpgCompileException(block.Line, "Program data does not identify a declared compile-time array.");
            if (field.InferredCompileTimeDimension && (block.Name is null || fixedSeen))
                throw new RpgCompileException(block.Line, "DIM(*CTDATA) requires named data preceding all fixed-dimension array data.");
            fixedSeen |= !field.InferredCompileTimeDimension;
            var members = alternates.TryGetValue(field.Name, out var alternate) ? new[] { field, alternate } : new[] { field };
            var widths = members.Select(CompileTimeWidth).ToArray(); var width = widths.Sum();
            var initialized = members.Select(_ => new List<string>()).ToArray(); var locations = new List<int>();
            var previous = new object?[members.Length];
            foreach (var record in block.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (initialized[0].Count == field.Dimension) throw new RpgCompileException(record.Line, "Compile-time data exceeds the declared array dimension.");
                var count = Math.Min(field.ElementsPerRecord, field.Dimension - initialized[0].Count);
                for (var index = 0; index < count; index++)
                {
                    var start = index * width;
                    for (var member = 0; member < members.Length; member++)
                    {
                        var text = start >= record.Text.Length ? "" : record.Text.Substring(start, Math.Min(widths[member], record.Text.Length - start));
                        var value = CompileTimeValue(members[member], text.PadRight(widths[member]), record.Line);
                        CheckCompileTimeSequence(members[member], previous[member], value, record.Line); previous[member] = value;
                        initialized[member].Add(CompileTimeLiteral(members[member], value)); start += widths[member];
                    }
                    locations.Add(record.Line);
                }
                var unusedStart = count * width; var unusedEnd = Math.Min(record.Text.Length, field.ElementsPerRecord * width);
                if (count < field.ElementsPerRecord && record.Text.AsSpan(Math.Min(unusedStart, record.Text.Length), Math.Max(0, unusedEnd - unusedStart)).ContainsAnyExcept(' '))
                    throw new RpgCompileException(record.Line, "Unused entries in the final compile-time record must be blank.");
            }
            for (var member = 0; member < members.Length; member++)
            {
                var element = program.FindDsElement(members[member].Name)!;
                if (initialized[member].Count < members[member].Dimension)
                    CheckCompileTimeSequence(members[member], previous[member], RpgRuntimeContext.ClearValueFor(members[member]), block.Line);
                element.CompileTimeInitializers = initialized[member]; element.CompileTimeLines = locations;
            }
        }
        foreach (var field in arrays.Where(f => f.InferredCompileTimeDimension))
            if (!program.CompileTimeData.Any(d => d.Name == field.Name)) throw new RpgCompileException(0, "DIM(*CTDATA) requires named compile-time data.");
    }
    private static Dictionary<string, RpgField> BindAlternatingArrays(RpgProgram program)
    {
        var alternates = new Dictionary<string, RpgField>(StringComparer.OrdinalIgnoreCase);
        foreach (var alternate in program.Fields.Where(f => !program.GlobalFields.Contains(f) && f.AlternateOf is not null))
        {
            var line = program.FindDsElement(alternate.Name)!.DeclarationLine;
            if (program.FindField(alternate.AlternateOf!) is not { CompileTimeArray: true, AlternateOf: null } main || !main.NativeArray && !main.IsTable)
                throw new RpgCompileException(line, "ALT requires a main native compile-time array; missing targets, chains and cycles are unsupported.");
            if (main.Dimension != alternate.Dimension || main.InferredCompileTimeDimension != alternate.InferredCompileTimeDimension)
                throw new RpgCompileException(line, "Alternating arrays require matching dimensions; both must use DIM(*CTDATA) when either does.");
            if (!alternates.TryAdd(main.Name, alternate)) throw new RpgCompileException(line, "A main array can have only one alternate array.");
            if ((long)(CompileTimeWidth(main) + CompileTimeWidth(alternate)) * main.ElementsPerRecord > 100)
                throw new RpgCompileException(line, "Alternating pairs per record exceed 100 source positions.");
        }
        return alternates;
    }
    private static void CheckCompileTimeSequence(RpgField field, object? previous, object value, int line)
    {
        // Character order depends on the activation CCSID and is checked against native bytes at initialization.
        if (previous is null || field.ArrayDescending is not { } descending || field.Kind == RpgFieldKind.Character) return;
        var compared = RpgValues.Compare(previous, value);
        if (descending ? compared < 0 : compared > 0) throw new RpgCompileException(line, "Compile-time data does not follow its declared ASCEND/DESCEND sequence.");
    }
    private static object CompileTimeValue(RpgField field, string text, int line)
    {
        if (field.Kind == RpgFieldKind.Character) return text;
        if (text.All(c => c == ' ')) return RpgRuntimeContext.ClearValueFor(field);
        if (field.Kind == RpgFieldKind.Indicator)
            return text is "0" or "1" ? text == "1" : throw new RpgCompileException(line, "Compile-time indicator data requires 0 or 1.");
        if (field.Kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp)
        {
            var format = field.Kind == RpgFieldKind.Date ? "yyyy-MM-dd" : field.Kind == RpgFieldKind.Time ? "HH:mm:ss" : "yyyy-MM-dd-HH.mm.ss.ffffff";
            if (!DateTime.TryParseExact(text, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                throw new RpgCompileException(line, "Compile-time date/time data must use the declared default ISO format.");
            return field.Kind == RpgFieldKind.Time ? (object)date.TimeOfDay : new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), TimeSpan.Zero);
        }
        if (field.Kind == RpgFieldKind.Float)
        {
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number) || field.Length == 4 && !float.IsFinite((float)number))
                throw new RpgCompileException(line, "Invalid or out-of-range compile-time float data.");
            return number;
        }
        var digits = text.Trim(' '); var negative = false;
        if (field.CompileTimeFormat is "L" or "R")
        {
            var sign = field.CompileTimeFormat == "L" ? text[0] : text[^1];
            if (sign is not ('+' or '-')) throw new RpgCompileException(line, "EXTFMT(L/R) requires an explicit sign in each data entry.");
            negative = sign == '-'; digits = (field.CompileTimeFormat == "L" ? text[1..] : text[..^1]).Trim(' ');
        }
        else if (digits.Length > 0 && !char.IsAsciiDigit(digits[^1]))
        {
            var last = digits[^1]; var positive = "{ABCDEFGHI".IndexOf(last); var minus = "}JKLMNOPQR".IndexOf(last);
            if (positive < 0 && minus < 0) throw new RpgCompileException(line, "Invalid zoned compile-time numeric data.");
            negative = minus >= 0; digits = digits[..^1] + (char)('0' + Math.Max(positive, minus));
        }
        if (digits.Length == 0 || digits.Any(c => !char.IsAsciiDigit(c)) || !decimal.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var coefficient))
            throw new RpgCompileException(line, "Compile-time numeric data exceeds supported digits or decimal precision.");
        var value = coefficient * RpgNumbers.Pow10(field.Decimals) * (negative ? -1 : 1);
        ValidateDeclarationInitializer(value, field.Kind, field.Length, field.Decimals, false, line);
        if (field.Kind == RpgFieldKind.Integer)
        {
            var bytes = ScalarDeclarationWidth(field); var min = bytes == 8 ? long.MinValue : -(1L << (bytes * 8 - 1)); var max = bytes == 8 ? long.MaxValue : (1L << (bytes * 8 - 1)) - 1;
            if (value < min || value > max) throw new RpgCompileException(line, "Compile-time integer data exceeds its native byte width.");
        }
        return value;
    }
    private static string CompileTimeLiteral(RpgField field, object value) => field.Kind switch
    {
        RpgFieldKind.Character => (string)value,
        RpgFieldKind.Date => DeclarationLiteral(RpgValues.ToDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        RpgFieldKind.Time => DeclarationLiteral(RpgValues.ToTime(value).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)),
        RpgFieldKind.Timestamp => DeclarationLiteral(RpgValues.ToDate(value).ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture)),
        _ => DeclarationLiteral(value)
    };
}
