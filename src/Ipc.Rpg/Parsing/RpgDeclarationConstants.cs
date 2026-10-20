using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static void DeclareFreeConstant(RpgProgram program, string text, int line)
    {
        var match = Regex.Match(text, @"\ADCL-C\s+([A-Za-z_][A-Za-z0-9_]*)\s+(.+)\z", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) throw new RpgCompileException(line, "DCL-C requires a name and constant value.");
        var name = match.Groups[1].Value.ToUpperInvariant(); var expression = match.Groups[2].Value;
        if (program.IsNativeProcedure) program.Fields.RemoveAll(f => program.GlobalFields.Contains(f) && f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (!RpgWorkstationBindings.Name(name) || HasLocalDeclaration(program, name))
            throw new RpgCompileException(line, "Invalid or duplicate named constant.");
        if (Regex.IsMatch(expression, @"\ACONST\b", RegexOptions.IgnoreCase))
        {
            var options = DeclarationKeywords(expression, line);
            if (options.Count != 1 || options[0].Arguments is not { Length: > 0 } argument)
                throw new RpgCompileException(line, "CONST requires one constant value.");
            expression = argument;
        }
        var value = DeclarationValue(program, expression, line); var layout = ValueLayout(value, line);
        if (layout.Length > 32767 || program.Fields.Count(f => f.Source == RpgFieldSource.Constant && f.InitializationExpression is not null) >= 1024 ||
            program.Fields.Where(f => f.Source == RpgFieldSource.Constant && f.InitializationExpression is not null).Sum(f => (long)f.Length) + layout.Length > 1048576)
            throw new RpgCompileException(line, "Named constants exceed 32767 units per value, 1024 constants or 1 MiB per program.");
        program.Fields.Add(new() { Name = name, Kind = layout.Kind, Length = layout.Length, Decimals = layout.Decimals,
            Source = RpgFieldSource.Constant, OpaqueBytes = value is byte[], InitializationExpression = DeclarationLiteral(value) });
    }

    private static object DeclarationValue(RpgProgram program, string text, int line, RpgDataStructure? current = null)
    {
        try
        {
            // These context-sensitive figurative constants need a target type, unlike literal text with the same spelling.
            var unquoted = Regex.Replace(text, @"'(?:[^']|'')*'", "''");
            if (Regex.IsMatch(unquoted, @"\*(?:LOVAL|HIVAL)\b", RegexOptions.IgnoreCase))
                throw new RpgCompileException(line, "Context-sensitive figurative constants are unsupported in declarations.");
            return Constant(RpgExpressionParser.Parse(text), 0);
        }
        catch (RpgCompileException error) when (error.LineNumber == 0 && error.Location is null)
        { throw new RpgCompileException(line, error.Message); }
        catch (Exception error) when (error is OverflowException or FormatException or ArithmeticException)
        { throw new RpgCompileException(line, "Invalid declaration constant: " + error.Message); }

        object Constant(RpgExpr expression, int depth)
        {
            if (depth > 64) throw new RpgCompileException(line, "Declaration constant nesting exceeds 64 levels.");
            switch (expression)
            {
                case RpgLiteral { Value: not null } literal: return literal.Value;
                case RpgHexLiteral hex: return hex.ToArray();
                case RpgFieldRef field when program.PreviewConstantLookup is { } lookup:
                    if (lookup(field.Name) is not { InitializationExpression: { } future })
                        throw new RpgCompileException(line, "A declaration type requires an earlier named constant: " + field.Name + ".");
                    return Constant(RpgExpressionParser.Parse(future), depth + 1);
                case RpgFieldRef field when program.FindField(field.Name) is { Source: RpgFieldSource.Constant, InitializationExpression: { } initial }:
                    return Constant(RpgExpressionParser.Parse(initial), depth + 1);
                case RpgUnary { Op: "+" or "-" } unary:
                    var operand = Constant(unary.Operand, depth + 1);
                    if (!RpgValues.IsNumeric(operand)) throw new RpgCompileException(line, "A declaration sign requires a numeric constant.");
                    return unary.Op == "+" ? operand : RpgValues.IsFloat(operand) ? (object)-RpgValues.ToDouble(operand) : -RpgValues.ToDecimal(operand);
                case RpgBuiltin builtin:
                    var arguments = builtin.Arguments;
                    if (builtin.Name == "PARMNUM" && arguments.Count == 1 && arguments[0] is RpgFieldRef parameter &&
                        program.HasNativeEntryInterface && program.NativeParameterLists.TryGetValue("/*ENTRY", out var names))
                    {
                        for (var index = 0; index < names.Count; index++)
                            if (names[index].Equals(parameter.Name, StringComparison.OrdinalIgnoreCase)) return (decimal)(index + 1 + (program.NativeReturnField?.ReturnByParameter == true ? 1 : 0));
                    }
                    if (builtin.Name == "ABS" && arguments.Count == 1)
                    {
                        var number = Constant(arguments[0], depth + 1);
                        if (!RpgValues.IsNumeric(number)) throw new RpgCompileException(line, "%ABS requires a numeric declaration constant.");
                        return RpgValues.IsFloat(number) ? (object)Math.Abs(RpgValues.ToDouble(number)) : decimal.Abs(RpgValues.ToDecimal(number));
                    }
                    if (builtin.Name is "SIZE" or "LEN" or "ELEM" or "DECPOS" && arguments.Count >= 1 &&
                        arguments.Count <= (builtin.Name == "SIZE" ? 2 : 1))
                    {
                        var layout = Layout(arguments[0], depth + 1);
                        if (builtin.Name == "LEN") return (decimal)layout.Length;
                        if (builtin.Name == "DECPOS") return layout.Kind is RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal or RpgFieldKind.Integer or RpgFieldKind.Binary
                            ? (decimal)layout.Decimals : throw new RpgCompileException(line, "%DECPOS requires non-floating numeric declaration data.");
                        if (builtin.Name == "ELEM") return layout.MultipleOccurrence ? (decimal)layout.Occurrences : layout.Array || layout.Table ? (decimal)layout.Count :
                            throw new RpgCompileException(line, "%ELEM requires an array or multiple-occurrence structure.");
                        var all = arguments.Count == 2;
                        if (all && (arguments[1] is not RpgFieldRef flag || !flag.Name.Equals("*ALL", StringComparison.OrdinalIgnoreCase)))
                            throw new RpgCompileException(line, "The second %SIZE argument must be *ALL.");
                        if (all && !layout.Array && !layout.Table && !layout.MultipleOccurrence) throw new RpgCompileException(line, "%SIZE(*ALL) requires an array, table or multiple-occurrence structure.");
                        return (decimal)layout.Width * (all ? layout.MultipleOccurrence ? layout.Occurrences : layout.Count : 1);
                    }
                    throw new RpgCompileException(line, "Unsupported compile-time built-in or argument count: %" + builtin.Name + ".");
                default:
                    throw new RpgCompileException(line, "A declaration requires a literal, an earlier named constant or a supported constant-valued built-in; runtime values and arithmetic expressions are unsupported.");
            }
        }
        DeclarationLayout Layout(RpgExpr expression, int depth)
        {
            if (depth > 64) throw new RpgCompileException(line, "Declaration layout nesting exceeds 64 levels.");
            var previewName = expression switch { RpgFieldRef root => root.Name,
                RpgMemberRef { Target: RpgFieldRef owner } previewMember => owner.Name + "." + previewMember.Name, _ => null };
            if (previewName is not null && program.PreviewLayoutLookup?.Invoke(previewName, current) is { } preview)
            {
                if (preview.IsFileParameter) throw new RpgCompileException(line, "A file parameter has no data layout.");
                return new(preview.Kind, preview.Length, preview.Decimals, ScalarDeclarationWidth(preview), preview.IsArray,
                    preview.Dimension, preview.ParameterStructure, preview.ParameterStructure?.IsMultipleOccurrence == true,
                    preview.ParameterStructure?.Occurrences ?? 1, preview.IsTable);
            }
            if (expression is RpgFieldRef field)
            {
                if (program.FindField(field.Name) is { } scalar)
                {
                    if (scalar.IsFileParameter) throw new RpgCompileException(line, "A file parameter has no data layout.");
                    return new(scalar.Kind, scalar.Length, scalar.Decimals, ScalarDeclarationWidth(scalar), scalar.IsArray, scalar.Dimension, Table: scalar.IsTable);
                }
                if (program.FindDs(field.Name) is { } ds)
                {
                    var width = DeclarationStructureLength(program, ds);
                    return new(RpgFieldKind.Character, width, 0, width, ds.IsArray, ds.Dimension, ds, ds.IsMultipleOccurrence, ds.Occurrences);
                }
                var element = program.FindDsElement(field.Name) ?? (current is { Qualified: false } ? current.Find(field.Name) : null);
                if (element is not null) return ElementLayout(element);
            }
            if (expression is RpgMemberRef member)
            {
                var ds = member.Target is RpgFieldRef owner && current?.Name.Equals(owner.Name, StringComparison.OrdinalIgnoreCase) == true
                    ? current : Layout(member.Target, depth + 1).Structure;
                if (ds is null || !ds.Qualified || member.Target is RpgFieldRef && ds.IsArray || ds.Find(member.Name) is not { } element)
                    throw new RpgCompileException(line, "Invalid qualified compile-time layout reference.");
                return ElementLayout(element);
            }
            if (expression is RpgElementRef elementRef) return Indexed(Layout(elementRef.Target, depth + 1), elementRef.Index, depth + 1);
            if (expression is RpgDynamicIndexRef dynamic) return Indexed(Layout(new RpgFieldRef(dynamic.Name), depth + 1), dynamic.Index, depth + 1);
            if (expression is RpgIndexedRef index) return Indexed(Layout(new RpgFieldRef(index.Name), depth + 1), new RpgLiteral(index.Index), depth + 1);
            return ValueLayout(Constant(expression, depth + 1), line);
        }
        DeclarationLayout Indexed(DeclarationLayout layout, RpgExpr expression, int depth)
        {
            var value = Constant(expression, depth + 1);
            if (!layout.Array || !RpgValues.IsNumeric(value) || RpgValues.IsFloat(value) ||
                RpgValues.ToDecimal(value) != decimal.Truncate(RpgValues.ToDecimal(value)) || RpgValues.ToDecimal(value) < 1 || RpgValues.ToDecimal(value) > layout.Count)
                throw new RpgCompileException(line, "Invalid compile-time array index.");
            return layout with { Array = false, Count = 1 };
        }
    }

    private sealed record DeclarationLayout(RpgFieldKind Kind, int Length, int Decimals, int Width,
        bool Array = false, int Count = 1, RpgDataStructure? Structure = null, bool MultipleOccurrence = false, int Occurrences = 1, bool Table = false);
    private static DeclarationLayout ElementLayout(RpgDsElement field) => new(field.Kind, field.Length, field.Decimals,
        field.StorageLength, field.IsArray, field.Dimension, Structure: field.NestedStructure);
    private static int ScalarDeclarationWidth(RpgField field) => field.Kind switch
    {
        RpgFieldKind.Packed or RpgFieldKind.Decimal => (field.Length + 2) / 2,
        RpgFieldKind.Integer => field.Length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 },
        RpgFieldKind.Binary => field.Length <= 4 ? 2 : 4,
        _ => field.Length
    };
    private static int DeclarationStructureLength(RpgProgram program, RpgDataStructure ds)
    {
        if (program.GlobalStructures.Contains(ds) && program.GlobalProgram is { } globals) return DeclarationStructureLength(globals, ds);
        return Math.Max(ds.Length, Math.Max(program.DeclarationStructureLengths.GetValueOrDefault(ds.Name),
            ds.LikeDataStructure is { } parent && (program.FindDs(parent) ?? ExactActualType(program, RpgExpressionParser.Parse(parent))?.ParameterStructure) is { } layout ? DeclarationStructureLength(program, layout) : 0));
    }
    private static DeclarationLayout ValueLayout(object value, int line)
    {
        if (value is string text) return new(RpgFieldKind.Character, text.Length, 0, text.Length);
        if (value is byte[] bytes) return new(RpgFieldKind.Character, bytes.Length, 0, bytes.Length);
        if (value is bool) return new(RpgFieldKind.Indicator, 1, 0, 1);
        if (RpgValues.IsFloat(value)) return new(RpgFieldKind.Float, 8, 0, 8);
        if (RpgValues.IsNumeric(value))
        {
            var number = RpgValues.ToDecimal(value); var scale = (decimal.GetBits(number)[3] >> 16) & 255;
            var digits = Math.Max(Math.Max(1, scale), decimal.Abs(number).ToString("F" + scale, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length);
            return new(RpgFieldKind.Packed, digits, scale, (digits + 2) / 2);
        }
        throw new RpgCompileException(line, "Unsupported declaration constant type.");
    }
    private static string DeclarationLiteral(object value) => value switch
    {
        string text => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'",
        byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        bool flag => flag ? "*ON" : "*OFF",
        double number => number.ToString("R", CultureInfo.InvariantCulture) + (number.ToString("R", CultureInfo.InvariantCulture).Contains('E', StringComparison.OrdinalIgnoreCase) ? "" : "E0"),
        _ => RpgValues.ToDecimal(value).ToString(CultureInfo.InvariantCulture)
    };
    private static int DeclarationInteger(RpgProgram program, string text, int line, int minimum, int maximum, RpgDataStructure? current = null)
    {
        var value = DeclarationValue(program, text, line, current);
        if (!RpgValues.IsNumeric(value) || RpgValues.IsFloat(value)) throw new RpgCompileException(line, "Declaration dimension requires a non-float integer constant.");
        var number = RpgValues.ToDecimal(value);
        if ((decimal.GetBits(number)[3] & 0x00FF0000) != 0 || number != decimal.Truncate(number) || number < minimum || number > maximum)
            throw new RpgCompileException(line, "Declaration dimension is outside the supported integer/storage bound.");
        return (int)number;
    }
    private static void ValidateDeclarationInitializer(object value, RpgFieldKind kind, int length, int decimals, bool opaque, int line)
    {
        if (kind == RpgFieldKind.Character)
        {
            if (opaque ? value is byte[] bytes && bytes.Length == length || value is string { Length: 0 } : value is string text && text.Length <= length) return;
            throw new RpgCompileException(line, "Character INZ must fit its declared length; hexadecimal INZ must match its byte width.");
        }
        if (kind == RpgFieldKind.Indicator)
        {
            if (value is bool || value is "0" or "1") return;
            throw new RpgCompileException(line, "Indicator INZ requires *ON/*OFF or character 0/1.");
        }
        if (kind is RpgFieldKind.Date or RpgFieldKind.Time or RpgFieldKind.Timestamp)
        {
            if (value is string date && (kind == RpgFieldKind.Time ? TimeSpan.TryParse(date, CultureInfo.InvariantCulture, out var time) && time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) :
                DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _))) return;
            throw new RpgCompileException(line, "Unsupported date/time INZ literal.");
        }
        if (!RpgValues.IsNumeric(value)) throw new RpgCompileException(line, "Numeric INZ requires a numeric constant.");
        if (kind == RpgFieldKind.Float)
        {
            var number = RpgValues.ToDouble(value);
            if (!double.IsFinite(number) || length == 4 && !float.IsFinite((float)number))
                throw new RpgCompileException(line, "Float INZ exceeds its native byte width.");
            return;
        }
        try
        {
            var number = RpgValues.ToDecimal(value);
            if (kind == RpgFieldKind.Integer)
            {
                var bytes = length switch { 3 => 1, 5 => 2, 10 => 4, _ => 8 };
                var minimum = bytes == 8 ? long.MinValue : -(1L << (bytes * 8 - 1));
                var maximum = bytes == 8 ? long.MaxValue : (1L << (bytes * 8 - 1)) - 1;
                if (number < minimum || number > maximum) throw new RpgCompileException(line, "Integer INZ exceeds its native byte width.");
            }
            if (decimal.Round(number, decimals) != number || decimal.Abs(number).ToString("F" + decimals, CultureInfo.InvariantCulture).Replace(".", "").TrimStart('0').Length > length)
                throw new RpgCompileException(line, "INZ exceeds the declared numeric precision.");
        }
        catch (OverflowException) { throw new RpgCompileException(line, "INZ exceeds the declared numeric precision."); }
    }
}
