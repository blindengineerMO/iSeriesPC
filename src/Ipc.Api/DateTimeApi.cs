using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Services.Sqlite;

namespace Ipc.Api;

/// <summary>QWCCVTDT over native character/TOD layouts and the actual UTC/host clock.</summary>
public sealed partial class DateTimeApi(SqliteConnectionFactory factory, int ccsid = 37,
    CancellationToken cancellationToken = default, Func<DateTimeOffset>? clock = null, TimeZoneInfo? systemZone = null)
{
    private readonly ApiArguments _arguments = new(ccsid);
    private static readonly string[] CharacterFormats = ["*YMD", "*MDY", "*DMY", "*JUL", "*YYMD", "*MDYY", "*DMYY", "*LONGJUL"];
    private static readonly DateTime ZoneMinimum = new(1928, 8, 25);
    private static readonly DateTime ZoneMaximum = new(2071, 5, 9);

    public void Convert(IReadOnlyList<object?> p)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (p.Count is not (5 or 10 or 11)) throw new CpfException("CPF3C36", "QWCCVTDT requires five, ten or eleven parameters.");
        var error = new ApiArguments.ErrorReceiver(_arguments, p[4]);
        if (error.Target is { } target && p.Where((_, index) => index != 4).Any(value => ReferenceEquals(value, target)))
            throw ApiArguments.Bad("Error storage cannot alias another argument.");
        try
        {
            var inputControl = _arguments.Text(p[0], 10); var outputControl = _arguments.Text(p[2], 10);
            var systemFormat = inputControl == "*SYSVAL" || outputControl == "*SYSVAL" ? new SystemValueStore(factory).ReadSnapshot().Get("QDATFMT").Value : null;
            var inputFormat = Format(inputControl, true, systemFormat);
            var outputFormat = Format(outputControl, false, systemFormat);
            if (outputFormat == "*DOS" && inputFormat is not ("*CURRENT" or "*DTS")) throw new CpfException("CPF1850", "DOS output requires CURRENT or DTS input.");
            var output = _arguments.CharacterOutput(p[3]);
            var precision = p.Count >= 10 ? _arguments.Text(p[9], 1) : "0";
            if (precision is not ("0" or "1")) throw Invalid("Invalid precision indicator.");
            var segment = p.Count == 11 ? _arguments.Text(p[10], 1) : "1";
            if (segment is not ("0" or "1")) throw Invalid("Invalid repeated-time indicator.");
            var zoneLength = p.Count >= 10 ? _arguments.Binary32(p[8]) : 0;
            if (zoneLength < 0) throw Invalid("Invalid time-zone receiver length.");
            var zoneOutput = zoneLength == 0 ? null : _arguments.CharacterOutput(p[7]);
            if (zoneLength > zoneOutput?.Buffer.Length) throw Invalid("Time-zone receiver is too short.");
            ApiArguments.DistinctOutputs(new[] { output.Target, zoneOutput?.Target, error.Target }.OfType<Ipc.Core.Work.ProgramArgument>());
            var inputZoneName = inputFormat == "*CURRENT" ? "*UTC" : p.Count >= 10 ? _arguments.Text(p[5], 10) : "*SYS";
            var outputZoneName = p.Count >= 10 ? _arguments.Text(p[6], 10) : "*SYS";
            var inputZone = Zone(inputZoneName); var outputZone = Zone(outputZoneName);
            var changesZone = inputZoneName != outputZoneName; var needsZone = changesZone || zoneLength != 0;
            var size = Size(outputFormat, precision);
            if (size > output.Buffer.Length) throw Invalid("Output variable is too short.");
            byte[] bytes; DateTime date = default; TimeSpan? finalOffset = null;
            if (inputFormat == outputFormat && !needsZone)
                bytes = _arguments.Character(p[1], size).ToArray().AsSpan(0, size).ToArray();
            else
            {
                byte[]? time = null; byte? century = null;
                if (inputFormat == "*CURRENT")
                {
                    var instant = clock?.Invoke() ?? DateTimeOffset.UtcNow;
                    if (changesZone && (instant.UtcDateTime < ZoneMinimum || instant.UtcDateTime >= ZoneMaximum)) throw BadDate();
                    var local = TimeZoneInfo.ConvertTime(new DateTimeOffset(Round(instant.UtcDateTime, precision), TimeSpan.Zero), outputZone);
                    date = local.DateTime; finalOffset = local.Offset;
                }
                else if (inputFormat == "*DTS") date = QuantizeTimestamp(NativeTimestamp.Decode(_arguments.Character(p[1], 8).ToArray()));
                else
                {
                    var input = _arguments.Character(p[1], Size(inputFormat, precision)).ToArray().AsSpan(0, Size(inputFormat, precision)).ToArray();
                    var twoDigits = !Long(inputFormat); var copyCentury = twoDigits && !Long(outputFormat) && outputFormat != "*DTS" && !needsZone;
                    var dateWidth = twoDigits ? 7 : 8; time = input[dateWidth..];
                    if (copyCentury) { century = input[0]; input[0] = _arguments.Encode("1")[0]; }
                    if (twoDigits && !_arguments.Encode("0123456789").Contains(input[0])) throw new CpfException("CPF1848", "Century digit is not valid.");
                    date = Parse(inputFormat, Decode(input.AsSpan(0, dateWidth), "CPF1060"));
                    if (outputFormat is "*DTS" || needsZone) date = WithTime(date, Decode(time, "CPF1061"), precision);
                }
                if (inputFormat != "*CURRENT" && changesZone)
                {
                    if (date < ZoneMinimum || date >= ZoneMaximum) throw BadDate();
                    var converted = ChangeZone(date, inputZone, outputZone, segment, precision);
                    date = converted.DateTime; finalOffset = converted.Offset;
                }
                else if (inputFormat != "*CURRENT" && needsZone && date >= ZoneMinimum && date < ZoneMaximum && inputZone.IsInvalidTime(date)) throw BadDate();
                if (outputFormat == "*DTS")
                {
                    if (Long(inputFormat) && (date < new DateTime(1928, 8, 24) || date >= new DateTime(2071, 5, 10))) throw BadDate();
                    try { bytes = NativeTimestamp.Encode(QuantizeTimestamp(date), !Long(inputFormat) && inputFormat != "*CURRENT" && !needsZone); }
                    catch (ArgumentOutOfRangeException) { throw BadDate(); }
                }
                else if (outputFormat == "*DOS") bytes = Dos(Round(date, precision), finalOffset ?? Offset(date, outputZone, segment));
                else
                {
                    if (inputFormat is "*CURRENT" or "*DTS" || needsZone)
                    {
                        date = Round(date, precision); time = _arguments.Encode(date.ToString("HHmmss", CultureInfo.InvariantCulture) + Fraction(date, precision)); century = null;
                    }
                    bytes = _arguments.Encode(Date(outputFormat, date)).Concat(time!).ToArray();
                    if (century is { } original) bytes[0] = original;
                }
            }
            var writes = new List<ApiArguments.Assignment> { _arguments.Prefix(output, bytes, bytes.Length) };
            if (zoneOutput is not null)
            {
                var info = ZoneInformation(outputZoneName, date);
                writes.Add(_arguments.Prefix(zoneOutput, info, Math.Min(zoneLength, info.Length)));
            }
            if (error.Success() is { } success) writes.Add(success);
            cancellationToken.ThrowIfCancellationRequested(); ApiArguments.Apply(writes);
        }
        catch (CpfException failure) when (error.Report(failure)) { }
    }

    private static string Format(string value, bool input, string? systemFormat)
    {
        if (value == "*SYSVAL") value = systemFormat!;
        if (CharacterFormats.Contains(value, StringComparer.Ordinal) || value == "*DTS" || input && value == "*CURRENT" || !input && value == "*DOS") return value;
        throw new CpfException("CPF1850", "Unsupported date/time format; job DATFMT attributes are not yet backed.");
    }
    private TimeZoneInfo Zone(string name) => name switch { "*SYS" => systemZone ?? TimeZoneInfo.Local, "*UTC" => TimeZoneInfo.Utc,
        _ => throw Invalid("Unsupported time zone; job and named TIMZON objects are not yet backed.") };
    private string Decode(ReadOnlySpan<byte> bytes, string error)
    {
        try { return new Ipc.Core.Work.ProgramBuffer(bytes, ccsid).ToText(); }
        catch (CpfException failure) when (failure.MessageId == "IPC0136") { throw new CpfException(error, "Invalid date/time character bytes."); }
    }
    private static bool Long(string format) => format is "*YYMD" or "*MDYY" or "*DMYY" or "*LONGJUL";
    private static int Size(string format, string precision) => format == "*DTS" ? 8 : format == "*DOS" ? 11 : (Long(format) ? 17 : 16) + (precision == "1" ? 3 : 0);
    private static CpfException Invalid(string message) => new("CPF3C3C", message);
    private static CpfException BadDate() => new("CPF1060", "Date is not valid for the requested conversion.");
}
