using System.Text;
using Ipc.Db.Definitions;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using System.Text.RegularExpressions;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private long FileRecordNumber(RpgStatement statement, RpgRuntimeContext context)
    {
        if (statement.FileSearch is { Kind: not RpgFileSearchKind.Scalar })
            throw new RpgRuntimeException("RRN access requires one integer literal or numeric field with zero decimals.");
        var text = statement.Factor1?.Trim() ?? "";
        var expression = RpgExpressionParser.Parse(text);
        if (!Regex.IsMatch(text, @"\A[+-]?[0-9]+\z", RegexOptions.CultureInvariant))
        {
            var name = expression switch { RpgFieldRef field => field.Name, RpgIndexedRef field => field.Name, RpgDynamicIndexRef field => field.Name, _ => null };
            var scalar = name is null ? null : context.Program.FindField(name);
            var element = name is null ? null : context.Program.FindDsElement(name);
            var kind = scalar?.Kind ?? element?.Kind;
            var array = scalar?.IsArray == true || element is not null && context.Program.DataStructures.Any(ds => ds.Dimension > 1 && ds.Elements.Contains(element));
            var indexed = expression is RpgIndexedRef or RpgDynamicIndexRef;
            if ((scalar?.Decimals ?? element?.Decimals) != 0 || kind is not (RpgFieldKind.Packed or RpgFieldKind.Zoned or RpgFieldKind.Decimal or RpgFieldKind.Integer or RpgFieldKind.Binary or RpgFieldKind.Float))
                throw new RpgRuntimeException("RRN access requires one integer literal or numeric field with zero decimals.");
            if (array != indexed) throw new RpgRuntimeException("An RRN array field requires an element index; scalar RRN fields cannot be indexed.");
        }
        var value = expression.Eval(context);
        const long maximum = Ipc.Db.Store.DatabaseRecordCursor.MaximumRelativeRecordNumber;
        if (!KeyNumeric(value) || value is float or double && (!double.IsFinite(RpgValues.ToDouble(value)) || RpgValues.ToDouble(value) is < 1 or > maximum))
            throw new RpgRuntimeException("Relative record number is outside the supported member range.");
        var number = RpgValues.ToDecimal(value);
        if (number != decimal.Truncate(number) || number is < 1 or > maximum)
            throw new RpgRuntimeException("Relative record number is outside the supported member range.");
        return (long)number;
    }
    private IReadOnlyDictionary<string, object?>? FileSearchValues(RpgStatement statement, RpgRuntimeContext context, RpgFileCursor cursor)
    {
        var argument = statement.FileSearch;
        if (argument?.Kind == RpgFileSearchKind.Current || argument is null && string.IsNullOrWhiteSpace(statement.Factor1)) return null;
        var keys = cursor.Definition.PrimaryFormat.Fields.Where(field => field.Sequence > 0).OrderBy(field => field.Sequence).ToArray();
        if (keys.Length == 0) throw new RpgRuntimeException("A keyed search requires declared file keys.");
        IReadOnlyList<string> expressions; RpgDsElement[]? subfields = null;
        if (argument?.Kind == RpgFileSearchKind.DataStructure)
        {
            var ds = context.Program.FindDs(argument.DataStructure!) ?? throw new RpgRuntimeException("Key data structure is unavailable.");
            if (ds.Dimension != 1) throw new RpgRuntimeException("%KDS requires a scalar data structure in the current subset.");
            var count = argument.CountExpression is null ? ds.Elements.Count : KeyCount(Eval(argument.CountExpression, context));
            if (count < 1 || count > ds.Elements.Count || count > keys.Length) throw new RpgRuntimeException("%KDS count exceeds the data structure or file key count.");
            subfields = ds.Elements.Take(count).ToArray(); expressions = subfields.Select(field => field.Name).ToArray();
        }
        else expressions = argument?.Expressions ?? [statement.Factor1!];
        if (expressions.Count < 1 || expressions.Count > keys.Length) throw new RpgRuntimeException("Search argument exceeds the file key count.");
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < expressions.Count; index++)
        {
            var field = keys[index];
            if (argument?.Kind == RpgFileSearchKind.KeyList) RequireKeyListShape(context, expressions[index], field);
            var value = subfields is null ? Eval(expressions[index], context) : context.ReadKeySubfield(subfields[index]);
            if (argument?.Kind is RpgFileSearchKind.Values or RpgFileSearchKind.DataStructure)
                value = ConvertSearchValue(field, value);
            values.Add(field.Name, value);
        }
        return values;
    }
    private static int KeyCount(object? value)
    {
        if (!KeyNumeric(value)) throw new RpgRuntimeException("%KDS key count must be numeric.");
        if (value is float or double && (!double.IsFinite(RpgValues.ToDouble(value)) || RpgValues.ToDouble(value) is < 1 or > 128))
            throw new RpgRuntimeException("%KDS key count must be an integer from 1 through 128.");
        var count = RpgValues.ToDecimal(value);
        if (count != decimal.Truncate(count) || count is < 1 or > 128) throw new RpgRuntimeException("%KDS key count must be an integer from 1 through 128.");
        return (int)count;
    }
    private static bool KeyNumeric(object? value) => value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal or float or double;
    private static object? ConvertSearchValue(FieldSpec key, object? value)
    {
        if (value is null) throw new RpgRuntimeException("Null search attributes require native null-indicator support.");
        if (key.Type == FieldType.Alpha)
        {
            if (value is not string text) throw new RpgRuntimeException("Character key components require character expressions.");
            var encoding = (Encoding)Ipc.Core.Text.CodePage.FromCcsid(key.Ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
            var result = new StringBuilder(); var bytes = 0;
            try
            {
                foreach (var rune in text.EnumerateRunes())
                {
                    if (bytes == key.Length) break;
                    var piece = rune.ToString(); var size = encoding.GetByteCount(piece);
                    if (bytes + size > key.Length) break;
                    result.Append(piece); bytes += size;
                }
            }
            catch (EncoderFallbackException) { throw new RpgRuntimeException("Key expression cannot be represented in the file CCSID."); }
            return result.ToString(); // the shared key codec pads in the file CCSID
        }
        if (key.IsNumeric)
        {
            if (!KeyNumeric(value)) throw new RpgRuntimeException("Numeric key components require numeric expressions.");
            if (key.Type == FieldType.Float) return RpgValues.ToDouble(value);
            try { return decimal.Round(RpgValues.ToDecimal(value), key.Type == FieldType.Binary ? 0 : key.Decimals, MidpointRounding.ToZero); }
            catch (OverflowException) { throw new RpgRuntimeException("Numeric key expression exceeds supported decimal precision."); }
        }
        if (key.Type == FieldType.Logic && value is bool || key.Type == FieldType.Date && value is DateOnly or DateTime or DateTimeOffset ||
            key.Type == FieldType.Timestamp && value is DateTime or DateTimeOffset || key.Type == FieldType.Time && value is TimeSpan or TimeOnly)
            return value;
        throw new RpgRuntimeException("Search component type does not match its file key.");
    }
    private static void RequireKeyListShape(RpgRuntimeContext context, string name, FieldSpec key)
    {
        var field = context.Program.FindField(name); var element = context.Program.FindDsElement(name);
        if (field is { IsArray: true } || field is { OpaqueBytes: true } || field is null && element is null)
            throw new RpgRuntimeException("KLIST requires scalar fields with matching key attributes.");
        var kind = field?.Kind ?? element!.Kind; var length = field?.Length ?? element!.Length; var decimals = field?.Decimals ?? element!.Decimals;
        var same = key.Type switch {
            FieldType.Alpha => kind is RpgFieldKind.Character or RpgFieldKind.VaryingChar &&
                key.VariableLength == (kind == RpgFieldKind.VaryingChar) && (key.VariableLength || length == key.Length) && context.Ccsid == key.Ccsid,
            FieldType.Packed => kind is RpgFieldKind.Packed or RpgFieldKind.Decimal && length == key.Length && decimals == key.Decimals,
            FieldType.Zoned => kind == RpgFieldKind.Zoned && length == key.Length && decimals == key.Decimals,
            FieldType.Binary => decimals == 0 && (kind == RpgFieldKind.Binary ? length : kind == RpgFieldKind.Integer ? length switch { 3 => 1, 5 => 2, 10 => 4, 20 => 8, _ => 0 } : 0) == key.Length,
            FieldType.Float => kind == RpgFieldKind.Float && length == key.Length,
            FieldType.Logic => kind == RpgFieldKind.Indicator,
            FieldType.Date => kind == RpgFieldKind.Date,
            FieldType.Time => kind == RpgFieldKind.Time,
            FieldType.Timestamp => kind == RpgFieldKind.Timestamp,
            _ => false };
        if (!same) throw new RpgRuntimeException("KFLD attributes do not match file key " + key.Name + ".");
    }
}
