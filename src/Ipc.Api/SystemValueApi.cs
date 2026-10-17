using System.Buffers.Binary;
using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.System;
using Ipc.Services.Sqlite;

namespace Ipc.Api;

/// <summary>QWCRSVAL's offset-directory receiver over a single shared system-value snapshot.</summary>
public sealed class SystemValueApi(SqliteConnectionFactory factory, int ccsid = 37,
    CancellationToken cancellationToken = default, Func<DateTimeOffset>? clock = null)
{
    private readonly ApiArguments _arguments = new(ccsid);
    public static IReadOnlyList<string> SupportedNames { get; } = Array.AsReadOnly(new[] {
        "QALWUSRDMN", "QCCSID", "QCENTURY", "QDATE", "QDATETIME", "QDATFMT", "QDATSEP", "QDAY", "QDAYOFWEEK",
        "QHOUR", "QMAXSIGN", "QMINUTE", "QMONTH", "QPRTTXT", "QPWDEXPITV", "QPWDLMTREP", "QPWDLVL", "QPWDMAXLEN",
        "QPWDMINLEN", "QPWDRQDDGT", "QSECOND", "QSECURITY", "QSYSLIBL", "QTIME", "QTIMSEP", "QUSRLIBL", "QYEAR"
    });

    public void Retrieve(IReadOnlyList<object?> parameters)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (parameters.Count != 5) throw new CpfException("CPF3C36", "QWCRSVAL requires five parameters.");
        var error = new ApiArguments.ErrorReceiver(_arguments, parameters[4]);
        if (error.Target is { } errorTarget && parameters.Take(4).Any(value => ReferenceEquals(value, errorTarget)))
            throw ApiArguments.Bad("API error receiver cannot alias another parameter.");
        try
        {
            var receiver = _arguments.CharacterOutput(parameters[0]);
            ApiArguments.DistinctOutputs(error.Target is { } target ? [receiver.Target, target] : [receiver.Target]);
            var length = _arguments.Binary32(parameters[1]);
            if (length < 28 || length > receiver.Buffer.Length) throw new CpfException("CPF1861", "Invalid system-value receiver length.");
            var count = _arguments.Binary32(parameters[2]);
            if (count is < 1 or > 4096) throw new CpfException("CPF1862", "Retrieve between 1 and 4096 system values.");
            var names = _arguments.Character(parameters[3], count * 10).ToArray();
            var selected = new string[count];
            for (var n = 0; n < count; n++)
            {
                selected[n] = new Ipc.Core.Work.ProgramBuffer(names.AsSpan(n * 10, 10), ccsid).ToText().TrimEnd(' ');
                if (!SupportedNames.Contains(selected[n], StringComparer.Ordinal))
                    throw new CpfException("CPF1860", "System value is not available in this runtime: " + selected[n]);
            }
            var now = clock?.Invoke() ?? DateTimeOffset.Now;
            var snapshot = new SystemValueStore(factory).ReadSnapshot(now);
            var entries = selected.Select(name => Value(name, snapshot, now)).ToArray();
            var returned = 0; var tablesLength = 0;
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidate = Align(tablesLength) + 16 + entry.Data.Length;
                if (4 + 4 * (returned + 1) + candidate > length) break;
                tablesLength = candidate; returned++;
            }
            var bytes = new byte[4 + 4 * returned + tablesLength];
            Put(bytes, 0, returned); var position = 4 + 4 * returned;
            for (var n = 0; n < returned; n++)
            {
                position = Align(position); Put(bytes, 4 + n * 4, position);
                _arguments.Encode(selected[n].PadRight(10) + entries[n].Type + " ").CopyTo(bytes, position);
                Put(bytes, position + 12, entries[n].Data.Length); entries[n].Data.CopyTo(bytes, position + 16);
                position += 16 + entries[n].Data.Length;
            }
            var received = _arguments.Prefix(receiver, bytes, bytes.Length); var success = error.Success();
            cancellationToken.ThrowIfCancellationRequested();
            ApiArguments.Apply(success is null ? [received] : [received, success]);
        }
        catch (CpfException failure) when (error.Report(failure)) { }
    }

    private sealed record Entry(char Type, byte[] Data);
    private Entry Value(string name, SystemValueRegistry values, DateTimeOffset now)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string Stored(string key) => values.Get(key).Value;
        Entry Text(string text, int width)
        {
            byte[] bytes;
            try { bytes = _arguments.Encode(text); }
            catch (CpfException error) when (error.MessageId == "CPF3C3A") { throw Unrepresentable(name); }
            if (bytes.Length > width) throw Unrepresentable(name);
            var padded = Enumerable.Repeat(_arguments.Encode(" ")[0], width).ToArray(); bytes.CopyTo(padded, 0); return new('C', padded);
        }
        Entry Binary(string text)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) throw Unrepresentable(name);
            var data = new byte[4]; Put(data, 0, number); return new('B', data);
        }
        Entry Flag(string key) => Text(Stored(key) switch { "*YES" => "1", "*NO" => "0", _ => throw Unrepresentable(name) }, 1);
        Entry List(string key, int slots)
        {
            var names = Stored(key).Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (names.Length > slots || names.Any(value => value.Length > 10)) throw Unrepresentable(name);
            return Text(string.Concat(names.Select(value => value.PadRight(10))), slots * 10);
        }
        var julian = Stored(SystemValueNames.DateFormat).TrimStart('*') == "JUL";
        string Date(string format) => now.ToString(format, CultureInfo.InvariantCulture);
        return name switch {
            "QCCSID" => Binary(Stored(SystemValueNames.Ccsid)),
            "QSECURITY" => Text(Stored(SystemValueNames.SecurityLevel), 2),
            "QPWDLVL" => Binary(Stored(SystemValueNames.PasswordSystemLevel)),
            "QPWDMINLEN" => Binary(Stored(SystemValueNames.PasswordMinimumLength)),
            "QPWDMAXLEN" => Binary(Stored(SystemValueNames.PasswordSystemLevel) is "0" or "1" ? "10" : "128"),
            "QPWDRQDDGT" => Flag(SystemValueNames.PasswordRequiredDigit),
            "QPWDLMTREP" => Flag(SystemValueNames.PasswordRepeatedCharacters),
            "QPWDEXPITV" => Text(Stored(SystemValueNames.PasswordExpirationInterval) == "0" ? "*NOMAX" : Stored(SystemValueNames.PasswordExpirationInterval), 6),
            "QMAXSIGN" => Text(Stored(SystemValueNames.MaximumSignOnAttempts), 6),
            "QPRTTXT" => Text(Stored(SystemValueNames.PrintText), 30),
            "QDATFMT" => Text(Stored(SystemValueNames.DateFormat).TrimStart('*'), 3),
            "QDATSEP" => Text(Stored(SystemValueNames.DateSeparator), 1),
            "QTIMSEP" => Text(Stored(SystemValueNames.TimeSeparator), 1),
            "QSYSLIBL" => List(SystemValueNames.SystemLibraryList, 15),
            "QUSRLIBL" => List(SystemValueNames.UserLibraryList, 25),
            "QALWUSRDMN" => List(SystemValueNames.AllowUserDomain, 50),
            "QCENTURY" => Text((now.Year / 100 - 19).ToString(CultureInfo.InvariantCulture), 1),
            "QDATE" => Text((now.Year / 100 - 19).ToString(CultureInfo.InvariantCulture) + Date("yyMMdd"), 7),
            "QDATETIME" => Text(Date("yyyyMMddHHmmssffffff"), 20),
            "QTIME" => Text(Date("HHmmssfff"), 9),
            "QYEAR" => Text(Date("yy"), 2), "QMONTH" => Text(julian ? "" : Date("MM"), 2),
            "QDAY" => Text(julian ? now.DayOfYear.ToString("D3", CultureInfo.InvariantCulture) : Date("dd"), 3),
            "QDAYOFWEEK" => Text("*" + new[] { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" }[(int)now.DayOfWeek], 4),
            "QHOUR" => Text(Date("HH"), 2), "QMINUTE" => Text(Date("mm"), 2), "QSECOND" => Text(Date("ss"), 2),
            _ => throw new CpfException("CPF1860", "System value is not implemented.") };
    }
    private static CpfException Unrepresentable(string name) => new("CPF3C19", "Stored system value exceeds its native layout: " + name);
    private static int Align(int value) => (value + 3) & ~3;
    private static void Put(byte[] data, int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
