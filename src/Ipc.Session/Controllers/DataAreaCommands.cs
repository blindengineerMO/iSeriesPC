using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult CreateDataArea(CommandCall call) => CreateDataArea(call, null);
    private CommandResult CreateDataArea(CommandCall call, ClCommandContext? context)
    {
        string Text(string key, string fallback = "") => context?.Resolve(call.GetOption(key) ?? fallback) ?? CommandParser.Unquote(call.GetOption(key) ?? fallback);
        var (library, name) = ResolveWorkObject(Text("DTAARA").ToUpperInvariant(), ObjectType.DataArea, creating: true);
        var type = Text("TYPE", "*CHAR").ToUpperInvariant(); var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        var value = call.GetOption("VALUE") is { } initial ? DataAreaInput(initial, context) : null;
        var dimensions = call.Split("LEN");
        if (dimensions.Count == 1 && dimensions[0].StartsWith('(') && dimensions[0].EndsWith(')')) dimensions = CommandParser.Tokenize(dimensions[0][1..^1]);
        if (dimensions.Count > (type == "*DEC" ? 2 : 1)) throw new CpfException("IPC0003", "Invalid data-area dimensions.");
        var defaultLength = type switch { "*LGL" => 1, "*DEC" => 15, _ => value is ProgramBuffer raw ? raw.Length : value is string text ? Math.Max(1, StrictJobEncoding(ccsid).GetByteCount(text)) : 32 };
        int Dimension(int index, int fallback) => dimensions.Count <= index ? fallback : int.TryParse(dimensions[index], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1;
        var length = Dimension(0, defaultLength); var decimals = Dimension(1, type == "*DEC" && dimensions.Count == 0 ? 5 : 0);
        new DataAreaStore(_system.Connections).Create(library, name, type, length, decimals, value, ccsid, Text("TEXT"), Text("AUT", "*LIBCRTAUT"));
        return CommandResult.Ok("Data area " + library + "/" + name + " created.");
    }
    private CommandResult DeleteDataArea(CommandCall call)
    {
        var (library, name) = ResolveWorkObject(CommandParser.Unquote(call.GetOption("DTAARA") ?? "").ToUpperInvariant(), ObjectType.DataArea);
        _system.Objects.Delete(library, name, ObjectType.DataArea); return CommandResult.Ok("Data area " + library + "/" + name + " deleted.");
    }
    private object DataAreaInput(string raw, ClCommandContext? context)
    {
        raw = raw.Trim();
        if (context is not null && raw.StartsWith('&')) return context.Variable(raw).Value ?? "";
        if (raw.StartsWith("X'", StringComparison.OrdinalIgnoreCase))
            return ClExpression.Compile(raw).Evaluate(_ => throw new ClRuntimeException("Expected a hexadecimal constant."), _job?.Ccsid ?? _system.Config.Ccsid);
        if (Regex.IsMatch(raw, @"\A[+-]?(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)\z"))
            return ClExpression.Compile(raw).Evaluate(_ => throw new ClRuntimeException("Expected a constant numeric value."));
        return CommandParser.Unquote(raw);
    }
    private static Encoding StrictJobEncoding(int ccsid)
    {
        var encoding = (Encoding)CodePage.FromCcsid(ccsid).Clone();
        encoding.EncoderFallback = EncoderFallback.ExceptionFallback; encoding.DecoderFallback = DecoderFallback.ExceptionFallback; return encoding;
    }
    private static (string Name, int? Start, int? Length) DataAreaSelection(CommandCall call, Func<string, string>? resolve = null)
    {
        resolve ??= CommandParser.Unquote;
        var parts = call.Split("DTAARA");
        if (parts.Count == 1 && parts[0].StartsWith('(') && parts[0].EndsWith(')')) parts = CommandParser.Tokenize(parts[0][1..^1]);
        if (parts.Count is < 1 or > 3) throw new CpfException("IPC0126", "DTAARA requires a name and optional substring.");
        var name = resolve(parts[0]).Trim().ToUpperInvariant(); var range = parts.Skip(1).ToArray();
        if (range.Length == 1 && range[0].StartsWith('(') && range[0].EndsWith(')')) range = CommandParser.Tokenize(range[0][1..^1]).ToArray();
        if (range.Length == 0 || range.Length == 1 && resolve(range[0]).Trim().Equals("*ALL", StringComparison.OrdinalIgnoreCase)) return (name, null, null);
        if (range.Length != 2 || !int.TryParse(resolve(range[0]).Trim(), out var start) || !int.TryParse(resolve(range[1]).Trim(), out var length) || start is < 1 or > 2000 || length is < 1 or > 2000)
            throw new CpfException("IPC0126", "Data-area substring requires a start and length within 1–2000.");
        return (name, start, length);
    }
    private DataAreaValue ReadDataArea(CommandCall call, ClCommandContext? context)
    {
        var selection = DataAreaSelection(call, context?.Resolve);
        if (!selection.Name.StartsWith('*'))
        {
            var (library, name) = ResolveWorkObject(selection.Name, ObjectType.DataArea);
            return new DataAreaStore(_system.Connections).Read(library, name, selection.Start, selection.Length);
        }
        var (data, start, length, _) = SelectJobDataArea(call, context?.Resolve);
        return new("*CHAR", data.Length, 0, _job!.Ccsid, new ProgramBuffer(data.AsSpan(start - 1, length), _job.Ccsid));
    }
}
