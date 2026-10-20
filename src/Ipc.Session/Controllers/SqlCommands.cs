using System.Text;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Db.Sql;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteRunSql(CommandCall call)
    {
        string Argument(string keyword, int position, string fallback)
        {
            if (call.GetOption(keyword) is not null && call.Positional.Count > position) throw new CpfException("IPC0003", "Repeated " + keyword + ".");
            return CommandParser.Unquote(call.GetOption(keyword) ?? (call.Positional.Count > position ? call.Positional[position] : fallback));
        }
        var sql = Argument("SQL", 0, "");
        var commit = Argument("COMMIT", 1, "*CHG").ToUpperInvariant();
        if (commit is not ("*NONE" or "*NC")) throw new CpfException("SQL0104", "RUNSQL commitment mode " + commit + " is not implemented; native commitment control remains open.");
        var naming = Argument("NAMING", 2, "*SYS").ToUpperInvariant() switch {
            "*SYS" => SqlNaming.System, "*SQL" => SqlNaming.Sql, _ => throw new CpfException("IPC0003", "NAMING requires *SYS or *SQL.") };
        var ccsid = _job?.Ccsid ?? _system.Config.Ccsid;
        var encoding = (Encoding)Ipc.Core.Text.CodePage.FromCcsid(ccsid).Clone(); encoding.EncoderFallback = EncoderFallback.ExceptionFallback;
        try { if (encoding.GetByteCount(sql) is < 1 or > 5000) throw new CpfException("IPC0003", "SQL requires 1–5000 bytes in the job CCSID."); }
        catch (EncoderFallbackException) { throw new CpfException("IPC0003", "SQL cannot be represented in the job CCSID."); }
        var collection = CommandParser.Unquote(call.GetOption("DFTRDBCOL") ?? "*NONE").ToUpperInvariant();
        var options = new SqlExecutionOptions(naming, SearchLibraries("*CURLIB").First(), _job?.UserProfile ?? "QSECOFR",
            SearchLibraries("*LIBL").ToArray(), ccsid, collection == "*NONE" ? null : collection);
        var changed = _files.ExecuteSql(sql, options, _cancellationToken);
        return CommandResult.Ok($"SQL statement completed; {changed} rows affected.");
    }
}
