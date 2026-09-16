using System.Globalization;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed class RpgRuntimeContext
{
    private readonly RpgProgram _program;
    private RpgHost _host;
    internal void RebindHost(RpgHost host) => _host = host;
    private readonly Dictionary<string, object?> _slots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ipc.Core.Work.ProgramArgument> _references = new(StringComparer.OrdinalIgnoreCase);
    internal void BindReference(string name, Ipc.Core.Work.ProgramArgument argument)
    {
        var field = _program.FindField(name) ?? throw new RpgRuntimeException($"Unknown entry field '{name}'.");
        if (field.IsArray || field.IsDataStructure || field.Source == RpgFieldSource.Constant || _program.FindDsElement(name) is not null)
            throw new RpgRuntimeException("Borrowed entry arguments currently require scalar fields.");
        var compatible = argument.Type switch {
            null => true,
            "*CHAR" => field.Kind == RpgFieldKind.Character && field.Length == argument.Length,
            "*DEC" => field.Kind is RpgFieldKind.Packed or RpgFieldKind.Decimal && field.Length == argument.Length && field.Decimals == argument.Decimals,
            "*INT" => field.Kind is RpgFieldKind.Integer or RpgFieldKind.Binary && field.Decimals == 0 &&
                (field.Length <= 5 ? 2 : field.Length <= 10 ? 4 : 8) == argument.Length,
            "*LGL" => field.Kind == RpgFieldKind.Indicator,
            "*FLT" => field.Kind == RpgFieldKind.Float && field.Length == argument.Length,
            _ => false };
        if (!compatible) throw new RpgRuntimeException("CL/RPG reference parameter types or lengths do not match.");
        _ = Coerce(field, argument.Value);
        _references.Add(field.Name, argument);
    }
    internal void ReleaseReferences()
    {
        try { foreach (var reference in _references) _slots[reference.Key] = reference.Value.Value; }
        finally { _references.Clear(); }
    }
    private readonly Dictionary<string, object?[]> _arrays = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _dsStorage = new(StringComparer.OrdinalIgnoreCase);

    public bool[] Indicators { get; } = new bool[100];

    public RpgRuntimeContext(RpgProgram program, RpgHost host)
    {
        _program = program;
        _host = host;
        Initialize();
    }

    public RpgProgram Program => _program;

    private DateTimeOffset CurrentTime => _host.Now ?? DateTimeOffset.UtcNow;

    private static string DsKey(string name, int index) => $"{name}\u0001{index}";

    private void Initialize()
    {
        foreach (var field in _program.Fields)
        {
            if (field.IsArray)
            {
                var array = new object?[field.Dimension];
                for (var i = 0; i < field.Dimension; i++)
                {
                    array[i] = InitialValue(field);
                }

                _arrays[field.Name] = array;
            }
            else
            {
                _slots[field.Name] = field.Kind == RpgFieldKind.Indicator
                    ? (ParseBoolean(field.InitialValue))
                    : InitialValue(field);
            }
        }

        foreach (var ds in _program.DataStructures)
        {
            for (var dim = 0; dim < ds.Dimension; dim++)
            {
                _dsStorage[DsKey(ds.Name, dim)] = new string(' ', ds.Length);
            }
        }
    }

    private object InitialValue(RpgField field)
    {
        var init = field.InitialValue;
        if (string.Equals(init, "*BLANK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(init, "*BLANKS", StringComparison.OrdinalIgnoreCase))
        {
            init = null;
        }
        else if (string.Equals(init, "*ZERO", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(init, "*ZEROS", StringComparison.OrdinalIgnoreCase))
        {
            init = "0";
        }

        return field.Kind switch
        {
            RpgFieldKind.Character => PadCharacter(init ?? string.Empty, field.Length),
            RpgFieldKind.VaryingChar => init ?? string.Empty,
            RpgFieldKind.ProcPtr => init ?? string.Empty,
            RpgFieldKind.Zoned or RpgFieldKind.Packed or RpgFieldKind.Decimal => ParseDecimal(init) ?? 0m,
            RpgFieldKind.Binary or RpgFieldKind.Integer => ParseDecimal(init).HasValue ? (object)long.Parse(init!, CultureInfo.InvariantCulture) : 0L,
            RpgFieldKind.Float => ParseDouble(init) ?? 0d,
            RpgFieldKind.Date => RpgValues.ToDate(init),
            RpgFieldKind.Time => RpgValues.ToTime(init),
            RpgFieldKind.Timestamp => RpgValues.ToDate(init),
            RpgFieldKind.Indicator => ParseBoolean(init),
            _ => string.Empty,
        };
    }

    private void InitializeConstant(RpgField field, string value)
    {
        _slots[field.Name] = field.Kind == RpgFieldKind.Character
            ? PadCharacter(value, field.Length)
            : ParseNumeric(value);
    }

    public void BindConstants(IEnumerable<KeyValuePair<string, string>> constants)
    {
        foreach (var constant in constants)
        {
            var field = _program.FindField(constant.Key);
            if (field is not null && field.Source == RpgFieldSource.Constant)
            {
                InitializeConstant(field, constant.Value);
            }
        }
    }

    public bool HasField(string name) =>
        _program.FindField(name) is not null ||
        _program.FindDsElement(name) is not null ||
        _program.FindDs(name) is not null;

    public object? ReadValue(string name)
    {
        var element = _program.FindDsElement(name);
        if (element is not null)
        {
            return ReadDsElementBuffer(element, name);
        }

        var ds = _program.FindDs(name);
        if (ds is not null)
        {
            return GetDsBuffer(ds.Name, 0);
        }

        var field = _program.FindField(name)
            ?? throw new RpgRuntimeException($"Unknown field '{name}'.");
        if (field.IsArray)
        {
            return _arrays[field.Name];
        }

        return _references.TryGetValue(field.Name, out var reference) ? Coerce(field, reference.Value) : _slots[field.Name];
    }

    public void WriteValue(string name, object? value)
    {
        var element = _program.FindDsElement(name);
        if (element is not null)
        {
            WriteDsElementBuffer(element, name, 0, value);
            return;
        }

        var field = _program.FindField(name)
            ?? throw new RpgRuntimeException($"Unknown field '{name}'.");
        if (field.IsArray)
        {
            throw new RpgRuntimeException($"Cannot assign to array field '{name}' without an index.");
        }

        if (field.Source == RpgFieldSource.Constant)
        {
            throw new RpgRuntimeException($"Cannot assign to constant field '{name}'.");
        }

        var converted = Coerce(field, value);
        if (_references.TryGetValue(field.Name, out var reference)) reference.Value = converted;
        else _slots[field.Name] = converted;
    }

    public object? ReadArrayValue(string name, long index)
    {
        var element = _program.FindDsElement(name);
        if (element is not null)
        {
            return ReadDsElementBuffer(element, name, (int)(index - 1));
        }

        var field = _program.FindField(name)
            ?? throw new RpgRuntimeException($"Unknown field '{name}'.");
        if (!field.IsArray)
        {
            return field.Kind == RpgFieldKind.Character
                ? SubstringCharacter(CoerceToString(ReadValue(field.Name)), index)
                : ReadValue(field.Name);
        }

        if (index < 1 || index > field.Dimension)
        {
            throw new RpgRuntimeException($"Array index {index} out of range for '{name}' [{field.Dimension}].");
        }

        return _arrays[field.Name][index - 1];
    }

    public void WriteArrayValue(string name, long index, object? value)
    {
        var element = _program.FindDsElement(name);
        if (element is not null)
        {
            WriteDsElementBuffer(element, name, (int)(index - 1), value);
            return;
        }

        var field = _program.FindField(name)
            ?? throw new RpgRuntimeException($"Unknown field '{name}'.");
        if (!field.IsArray)
        {
            throw new RpgRuntimeException($"Cannot index non-array field '{name}'.");
        }

        if (index < 1 || index > field.Dimension)
        {
            throw new RpgRuntimeException($"Array index {index} out of range for '{name}' [{field.Dimension}].");
        }

        _arrays[field.Name][index - 1] = Coerce(field, value);
    }

    public void ClearValue(string name)
    {
        var element = _program.FindDsElement(name);
        if (element is not null)
        {
            var ds = _program.DataStructures.FirstOrDefault(d => d.Elements.Contains(element));
            if (ds is not null)
            {
                for (var dim = 0; dim < ds.Dimension; dim++)
                {
                    WriteDsElementBuffer(element, name, dim, ClearValueFor(element));
                }
            }

            return;
        }

        var field = _program.FindField(name)
            ?? throw new RpgRuntimeException($"Unknown field '{name}'.");
        if (field.IsArray)
        {
            for (var i = 0; i < field.Dimension; i++)
            {
                _arrays[field.Name][i] = ClearValueFor(field);
            }

            return;
        }

        WriteValue(field.Name, ClearValueFor(field));
    }

    public static object ClearValueFor(RpgFieldKind kind) => kind switch
    {
        RpgFieldKind.Zoned or RpgFieldKind.Packed or RpgFieldKind.Decimal or RpgFieldKind.Binary or RpgFieldKind.Integer => 0m,
        RpgFieldKind.Float => 0d,
        RpgFieldKind.Date or RpgFieldKind.Timestamp => RpgValues.ToDate(null),
        RpgFieldKind.Time => TimeSpan.Zero,
        RpgFieldKind.Indicator => false,
        _ => string.Empty,
    };

    public static object ClearValueFor(RpgField field) =>
        ClearValueFor(field.Kind);

    public static object ClearValueFor(RpgDsElement element) =>
        ClearValueFor(element.Kind);

    public bool HasVariable(string name) => _slots.ContainsKey(name) || _arrays.ContainsKey(name);

    private static string PadCharacter(string value, int length)
    {
        if (value.Length >= length)
        {
            return value[..length];
        }

        return value.PadRight(length);
    }

    private static string CoerceToString(object? value) => RpgValues.ToText(value);

    private static string SubstringCharacter(string text, long index)
    {
        if (index < 1 || index > text.Length)
        {
            return string.Empty;
        }

        return text[(int)index - 1].ToString();
    }

    private object? Coerce(RpgField field, object? value)
    {
        if (value is Ipc.Core.Work.ProgramBuffer buffer)
        {
            if (field.Kind is not (RpgFieldKind.Character or RpgFieldKind.VaryingChar)) throw new RpgRuntimeException("RPG numeric byte-buffer arguments require a typed ABI adapter.");
            try { value = buffer.ToText(); }
            catch (Ipc.Core.Messages.CpfException error) { throw new RpgRuntimeException(error.Message); }
        }
        return field.Kind switch
        {
        RpgFieldKind.Character => CoerceToString(ParseBlank(value)).PadRight(field.Length)[..field.Length],
        RpgFieldKind.VaryingChar or RpgFieldKind.ProcPtr => CoerceToString(ParseBlank(value)),
        RpgFieldKind.Zoned or RpgFieldKind.Packed or RpgFieldKind.Decimal => Round(ParseDecimalValue(value), field.Decimals),
        RpgFieldKind.Binary or RpgFieldKind.Integer => RpgValues.ToLong(value),
        RpgFieldKind.Float => RpgValues.ToDouble(value),
        RpgFieldKind.Date => RpgValues.ToDate(value),
        RpgFieldKind.Time => RpgValues.ToTime(value),
        RpgFieldKind.Timestamp => RpgValues.ToDate(value),
        RpgFieldKind.Indicator => ParseBoolean(value),
        _ => value,
    };
    }

    private static object? ParseBlank(object? value) =>
        value is string s && (s == "*BLANK" || s == "*BLANKS") ? string.Empty : value;

    private static decimal Round(object? value, int decimals)
    {
        var result = RpgValues.ToDecimal(value);
        if (decimals > 0)
        {
            result = decimal.Round(result, decimals, MidpointRounding.AwayFromZero);
        }
        else
        {
            result = decimal.Truncate(result);
        }

        return result;
    }

    private static decimal ParseDecimalValue(object? value)
    {
        if (value is string s && (s is "*BLANK" or "*BLANKS"))
        {
            return 0m;
        }

        return RpgValues.ToDecimal(value);
    }

    private static decimal? ParseDecimal(string? init) =>
        init is null
            ? null
            : decimal.TryParse(init, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static double? ParseDouble(string? init) =>
        init is null
            ? null
            : double.TryParse(init, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static object ParseNumeric(string value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : (object)double.Parse(value, CultureInfo.InvariantCulture);

    private static bool ParseBoolean(object? value)
    {
        if (value is bool b)
        {
            return b;
        }

        return value is string s && s is "" or "*ON" or "*YES" or "1" or "Y";
    }

    private RpgDsElement? FindElementBuffer(string name, out int offset, out int length, out string dsName, out int dimension)
    {
        var element = _program.FindDsElement(name);
        if (element is null)
        {
            offset = 0;
            length = 0;
            dsName = string.Empty;
            dimension = 0;
            return null;
        }

        offset = element.Offset;
        length = element.Length;
        var ds = _program.DataStructures.First(d => d.Elements.Contains(element));
        dsName = ds.Name;
        dimension = ds.Dimension;
        return element;
    }

    private string? GetDsBuffer(string dsName, int index)
    {
        if (!_dsStorage.TryGetValue(DsKey(dsName, index), out var buffer))
        {
            throw new RpgRuntimeException($"Data structure '{dsName}({index + 1})' is out of range.");
        }

        return buffer;
    }

    private object? ReadDsElementBuffer(RpgDsElement element, string name, int dsIndex = 0)
    {
        var ds = _program.DataStructures.First(d => d.Elements.Contains(element));
        var buffer = GetDsBuffer(ds.Name, dsIndex);
        var slice = sliceFromBuffer(buffer, element.Offset, element.Length);
        return CoerceRead(element, slice);
    }

    private void WriteDsElementBuffer(RpgDsElement element, string name, int dsIndex, object? value)
    {
        var ds = _program.DataStructures.First(d => d.Elements.Contains(element));
        var buffer = GetDsBuffer(ds.Name, dsIndex);
        var text = CoerceSliceWrite(element, value);
        var updated = text.Length >= element.Length
            ? text[..element.Length]
            : text.PadRight(element.Length);
        var next = buffer![..element.Offset] + updated + buffer[(element.Offset + element.Length)..];
        _dsStorage[DsKey(ds.Name, dsIndex)] = next;
    }

    private object? CoerceRead(RpgDsElement element, string slice)
    {
        var trimmed = slice.TrimStart();
        return element.Kind switch
        {
            RpgFieldKind.Character => slice,
            RpgFieldKind.VaryingChar => trimmed,
            RpgFieldKind.Zoned or RpgFieldKind.Packed or RpgFieldKind.Decimal =>
                decimal.TryParse(trimmed, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                    ? Round(parsed, element.Decimals)
                    : 0m,
            RpgFieldKind.Binary or RpgFieldKind.Integer =>
                long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong)
                    ? parsedLong
                    : 0L,
            RpgFieldKind.Float =>
                double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedDouble)
                    ? parsedDouble
                    : 0d,
            RpgFieldKind.Date or RpgFieldKind.Timestamp => RpgValues.ToDate(trimmed),
            RpgFieldKind.Time => RpgValues.ToTime(trimmed),
            RpgFieldKind.Indicator => slice.Trim() is not ("" or "0" or "N"),
            _ => slice,
        };
    }

    private string CoerceSliceWrite(RpgDsElement element, object? value)
    {
        return element.Kind switch
        {
            RpgFieldKind.Character => RpgValues.ToText(value),
            RpgFieldKind.VaryingChar => RpgValues.ToText(value),
            RpgFieldKind.Zoned or RpgFieldKind.Packed or RpgFieldKind.Decimal =>
                RpgValues.NormalizeDecimal(Round(ParseDecimalValue(value), element.Decimals)),
            RpgFieldKind.Binary or RpgFieldKind.Integer => RpgValues.ToLong(value).ToString(CultureInfo.InvariantCulture),
            RpgFieldKind.Float => RpgValues.ToDouble(value).ToString("G17", CultureInfo.InvariantCulture),
            RpgFieldKind.Date => RpgValues.ToDate(value).ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            RpgFieldKind.Timestamp => RpgValues.ToDate(value).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            RpgFieldKind.Time => RpgValues.ToTime(value).ToString("hhmmss", CultureInfo.InvariantCulture),
            RpgFieldKind.Indicator => RpgValues.ToBool(value) ? "1" : "0",
            _ => RpgValues.ToText(value),
        };
    }

    private static string sliceFromBuffer(string? buffer, int offset, int length)
    {
        if (buffer is null)
        {
            return string.Empty;
        }

        if (offset >= buffer.Length)
        {
            return new string(' ', length);
        }

        var available = Math.Min(length, buffer.Length - offset);
        return buffer.Substring(offset, available).PadRight(length);
    }

    public object? SpecialValue(string name) => name.ToUpperInvariant() switch
    {
        "*DATE" => CurrentTime.Date,
        "*TIME" => CurrentTime.TimeOfDay,
        "*TIMESTAMP" => CurrentTime,
        _ => throw new RpgRuntimeException($"Unknown special value '{name}'."),
    };

    public object? EvalBuiltin(string name, IReadOnlyList<RpgExpr> arguments)
    {
        var upper = name.ToUpperInvariant();
        switch (upper)
        {
            case "SUBST":
            case "SUBSTR":
                return EvalSubstring(arguments);
            case "LEN":
                RequireArgCount(name, arguments, 1);
                return arguments[0] is RpgFieldRef fr0 && Program.FindField(fr0.Name) is { Kind: RpgFieldKind.Character, IsArray: false } cf
                    ? (object)cf.Length
                    : (object)(RpgValues.ToText(arguments[0].Eval(this)).Length);
            case "SIZE":
                RequireArgCount(name, arguments, 1);
                return arguments[0] is RpgFieldRef fr1 && Program.FindField(fr1.Name) is { } sf
                    ? (object)(sf.IsArray ? sf.Length * sf.Dimension : sf.Length)
                    : (object)RpgValues.ToText(arguments[0].Eval(this)).Length;
            case "TRIM":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToText(arguments[0].Eval(this)).Trim();
            case "TRIML":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToText(arguments[0].Eval(this)).TrimStart();
            case "TRIMR":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToText(arguments[0].Eval(this)).TrimEnd();
            case "DEC":
                RequireArgCount(name, arguments, 3);
                return decimal.Round(RpgValues.ToDecimal(arguments[0].Eval(this)), (int)RpgValues.ToLong(arguments[2].Eval(this)), MidpointRounding.AwayFromZero);
            case "INT":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToLong(arguments[0].Eval(this));
            case "CHAR":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToText(arguments[0].Eval(this));
            case "ABS":
                RequireArgCount(name, arguments, 1);
                return arguments[0].Eval(this) is double d ? Math.Abs(d) : Math.Abs(RpgValues.ToDecimal(arguments[0].Eval(this)));
            case "DATE":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToDate(arguments[0].Eval(this));
            case "TIME":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToTime(arguments[0].Eval(this));
            case "TIMESTAMP":
                RequireArgCount(name, arguments, 1);
                return RpgValues.ToDate(arguments[0].Eval(this));
            case "FOUND":
                return false;
            case "EOF":
                return false;
            case "ERROR":
            case "STATUS":
                return string.Empty;
            case "PARMS":
            case "PARMNUM":
                return 0;
            default:
                throw new RpgRuntimeException($"Unsupported built-in function '%{name}'.");
        }
    }

    private object? EvalSubstring(IReadOnlyList<RpgExpr> arguments)
    {
        RequireArgCount("%SUBST", arguments, 2, 3);
        var text = RpgValues.ToText(arguments[0].Eval(this));
        var start = (int)RpgValues.ToLong(arguments[1].Eval(this));
        if (start < 1 || start > text.Length + 1)
        {
            return string.Empty;
        }

        var remaining = text.Length - start + 1;
        var length = arguments.Count == 3
            ? (int)Math.Min(remaining, Math.Max(0, RpgValues.ToLong(arguments[2].Eval(this))))
            : remaining;
        return text.Substring(start - 1, length);
    }

    private static void RequireArgCount(string name, IReadOnlyList<RpgExpr> arguments, int min, int? max = null)
    {
        if (arguments.Count < min || (max.HasValue && arguments.Count > max.Value))
        {
            throw new RpgRuntimeException($"%{name} expects {(max.HasValue ? $"{min} to {max.Value}" : min.ToString())} arguments.");
        }
    }
}
