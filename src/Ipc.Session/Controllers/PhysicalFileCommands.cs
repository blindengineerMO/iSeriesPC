using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteChangePhysicalFile(CommandCall call)
    {
        var (library, name) = ResolveFileReference(RequiredFile(call));
        var option = CommandParser.Unquote(call.GetOption("MAXMBRS") ?? "*SAME").ToUpperInvariant();
        int? maximum = option == "*SAME" ? null : option == "*NOMAX" ? 32767 : int.TryParse(option, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : 0;
        var result = _files.ChangePhysicalMemberLimit(library, name, maximum, ParseRecordWait(call.GetOption("WAITRCD"), change: true),
            ParseReuseDeleted(call.GetOption("REUSEDLT"), change: true), ParseMemberSize(call.GetOption("SIZE"), change: true));
        return CommandResult.Ok($"Physical file {library}/{name} maximum members: {result}.");
    }
    private CommandResult ExecuteChangeLogicalFile(CommandCall call)
    {
        var (library, name) = ResolveFileReference(RequiredFile(call));
        _files.ChangeLogicalRecordWait(library, name, ParseRecordWait(call.GetOption("WAITRCD"), change: true));
        return CommandResult.Ok($"Logical file {library}/{name} record wait updated.");
    }
    private CommandResult ExecuteInitializePhysicalMember(CommandCall call)
    {
        string Argument(string keyword, int position, string fallback)
        {
            var value = call.GetOption(keyword);
            if (value is not null && call.Positional.Count > position) throw new CpfException("IPC0003", "Duplicate " + keyword + " value.");
            return CommandParser.Unquote(value ?? (call.Positional.Count > position ? call.Positional[position] : fallback));
        }
        var file = Argument("FILE", 0, "");
        if (file.Length == 0) throw new CpfException("IPC0003", "INZPFM requires FILE.");
        var (library, name) = ResolveFileReference(file);
        var member = Argument("MBR", 1, "*FIRST");
        var records = Argument("RECORDS", 2, "*DFT").ToUpperInvariant();
        if (records is not ("*DFT" or "*DLT")) throw new CpfException("IPC0003", "RECORDS requires *DFT or *DLT.");
        var totalText = CommandParser.Unquote(call.GetOption("TOTRCDS") ?? "*NXTINCR").ToUpperInvariant();
        long? total = totalText == "*NXTINCR" ? null : long.TryParse(totalText, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
        long added;
        try { added = _files.InitializeMember(library, name, member, total, records == "*DLT", _cancellationToken); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode == 275)
        { throw new CpfException("CPF502F", "Check constraint violation on file member."); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (Ipc.Db.Store.DatabaseRecordErrors.IsReferentialConstraintFailure(error))
        { throw new CpfException("CPF502D", "Referential constraint violation on file member."); }
        return CommandResult.Ok($"Member in {library}/{name}: {added} records initialized.");
    }

    private static FileMemberSizeChange? ParseMemberSize(string? raw, bool change = false)
    {
        if (raw is null) return null;
        var values = CommandParser.Tokenize(raw).Select(value => CommandParser.Unquote(value).ToUpperInvariant()).ToArray();
        if (values.Length == 1 && values[0] == "*NOMAX") return new(Unlimited: true);
        if (values.Length is < 1 or > 3) throw new CpfException("IPC0003", "SIZE requires *NOMAX or an initial/increment/maximum list.");
        int? Value(int index, int fallback)
        {
            var text = values.Length > index ? values[index] : change ? "*SAME" : fallback.ToString(CultureInfo.InvariantCulture);
            if (change && text == "*SAME") return null;
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return number;
            throw new CpfException("IPC0003", "Invalid SIZE value.");
        }
        var initial = Value(0, 10000); var increment = Value(1, 1000); var maximum = Value(2, 3);
        if (initial is null && increment is null && maximum is null) return null;
        return new(initial, increment, maximum, initial is null ? null : false);
    }

    private static bool? ParseReuseDeleted(string? raw, bool change = false)
    {
        var text = CommandParser.Unquote(raw ?? (change ? "*SAME" : "*NO")).ToUpperInvariant();
        return text switch {
            "*YES" => true, "*NO" => false, "*SAME" when change => null,
            _ => throw new CpfException("IPC0138", "REUSEDLT requires *YES or *NO" + (change ? ", or *SAME." : "."))
        };
    }
    private static int? ParseRecordWait(string? raw, bool change = false, bool overriding = false)
    {
        if (raw is null) return change || overriding ? null : 60;
        var text = CommandParser.Unquote(raw).ToUpperInvariant();
        if (change && text == "*SAME") return null;
        if (text == "*IMMED") return 0;
        if (text == "*NOMAX") return overriding ? -1 : 32767;
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is >= 1 and <= 32767) return seconds;
        throw new CpfException("IPC0138", "WAITRCD requires 1–32767, *IMMED or *NOMAX" + (change ? ", or *SAME." : "."));
    }
    private static string RecordWaitText(int value) => value switch { -1 => "*NOMAX", 0 => "*IMMED", _ => value.ToString(CultureInfo.InvariantCulture) };
}
