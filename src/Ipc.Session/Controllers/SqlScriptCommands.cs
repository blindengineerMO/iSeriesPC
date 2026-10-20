using System.Globalization;
using System.Text.RegularExpressions;
using Ipc.Cl.Commands;
using Ipc.Cl.Compatibility;
using Ipc.Cl.Definitions;
using Ipc.Cl.Parsing;
using Ipc.Core.Compilation;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Services.Commands;
using Ipc.Services.Work;
using Ipc.Session.Sources;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteRunSqlStatements(CommandCall call)
    {
        string Value(string keyword, string fallback = "") => CommandParser.Unquote(call.GetOption(keyword) ?? fallback);
        string Choice(string keyword, string fallback, params string[] supported)
        {
            var value = Value(keyword, fallback).ToUpperInvariant();
            if (!supported.Contains(value)) throw new CpfException("SQL0104", "RUNSQLSTM " + keyword + "(" + value + ") is not implemented or invalid.");
            return value;
        }
        var process = Choice("PROCESS", "*RUN", "*RUN", "*SYN");
        var commit = Choice("COMMIT", "*CHG", "*CHG", "*UR", "*CS", "*ALL", "*RS", "*RR", "*NONE", "*NC");
        if (process == "*RUN" && commit is not ("*NONE" or "*NC")) throw new CpfException("SQL0104", "RUNSQLSTM native commitment control remains open; explicit *NONE/*NC is required.");
        var naming = Choice("NAMING", "*SYS", "*SYS", "*SQL") == "*SQL" ? SqlNaming.Sql : SqlNaming.System;
        _ = Choice("OPTION", "*LIST", "*NOLIST"); // Printing requires the unfinished spool service.
        _ = Choice("SECLVLTXT", "*NO", "*NO");
        if (!int.TryParse(Value("ERRLVL", "10"), NumberStyles.None, CultureInfo.InvariantCulture, out var level) || level is < 0 or > 40)
            throw new CpfException("IPC0003", "ERRLVL requires 0–40.");
        if (!int.TryParse(Value("MARGINS", "80"), NumberStyles.None, CultureInfo.InvariantCulture, out var margin) || margin is < 1 or > 32754)
            throw new CpfException("IPC0003", "MARGINS requires 1–32754.");
        var collection = Value("DFTRDBCOL", "*NONE").ToUpperInvariant();
        if (collection != "*NONE" && !ObjectName.IsValid(collection)) throw new CpfException("IPC0003", "Invalid default collection.");
        var stream = Value("SRCSTMF"); var file = Value("SRCFILE"); var member = Value("SRCMBR").ToUpperInvariant();
        if (stream.Length != 0 ? file.Length != 0 || member.Length != 0 : file.Length == 0 || !ObjectName.IsValid(member))
            throw new CpfException("IPC0003", "Specify SRCFILE/SRCMBR or SRCSTMF.");
        SourceDocument source;
        try
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (stream.Length != 0) source = new ProgramSourceResolver(_system, _job, _cancellationToken).Stream(stream);
            else
            {
                var key = ResolveFileReference(file.ToUpperInvariant());
                source = new(key.Library + "/" + key.Name + "(" + member + ")", _files.ReadSqlSource(key.Library, key.Name, member, margin, _job?.Ccsid ?? _system.Config.Ccsid, _cancellationToken));
            }
        }
        catch (Exception error) when (error is CpfException or IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            var id = error is CpfException cpf ? cpf.MessageId : "SQL0901";
            LogSqlScript(id, 30, "RUNSQLSTM source: " + error.Message);
            return CommandResult.Error("SQL9010: RUNSQLSTM source could not be read. " + error.Message, "SQL9010");
        }
        IReadOnlyList<SqlScriptStatement> statements;
        try { statements = SqlScript.Read(source.Text, _cancellationToken); }
        catch (CpfException error) { LogSqlScript(error.MessageId, error.Severity, error.Message); return CommandResult.Error("SQL9010: " + error.Message, "SQL9010"); }
        var stopped = false; var highest = 0; var completed = 0; long affected = 0; var diagnostics = new List<string>();
        foreach (var statement in statements)
        {
            _cancellationToken.ThrowIfCancellationRequested(); JobExecutionBudget.Checkpoint();
            var location = source.Identity + ":" + statement.Line + ":" + statement.Column;
            try
            {
                if (!statement.Terminated) throw new CpfException("SQL0104", "Each RUNSQLSTM statement must end with a semicolon.");
                var cl = Regex.Match(statement.Text, "^CL\\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                var syntaxOnly = process == "*SYN" || stopped;
                // *NONE ends each statement's automatic allocations. A script
                // must not retain completed row locks until the entire input ends.
                using var statementLocks = !syntaxOnly && _job is not null
                    ? new JobLockStore(_system.Connections).EnterCommand(_job.Key, _cancellationToken) : null;
                if (cl.Success)
                {
                    var command = CommandParser.Parse(statement.Text[cl.Length..]);
                    if (syntaxOnly)
                    {
                        var key = ResolveCommandObject(command.Name);
                        var definition = new CommandDefinitionStore(_system.Connections).Load(key.Library, key.Name.Value);
                        if (definition.Builtin is { } builtin && BuiltinContract.Validate(new CommandCall { Name = builtin, Keywords = command.Keywords, Positional = command.Positional }) is { } error)
                            throw new CpfException("IPC0003", error);
                        _ = CommandBinder.Bind(definition, command);
                    }
                    else
                    {
                        var result = DispatchCommand(command);
                        if (result.IsError) throw new CpfException(result.MessageId ?? "CPF0006", result.Message ?? "CL command failed.");
                        if (result.Outcome != CommandOutcome.Continue || result.WorkList is not null || result.Listing is not null)
                            throw new CpfException("CPF0006", "RUNSQLSTM CL command requires an interactive display.");
                        completed++;
                    }
                }
                else
                {
                    var options = SqlScriptOptions(naming, collection);
                    if (syntaxOnly)
                    {
                        if (SqlStatementParser.Parse(statement.Text, options) is SqlSelect)
                            throw new CpfException("SQL0104", "RUNSQLSTM does not execute SELECT result sets.");
                    }
                    else { affected += _files.ExecuteSql(statement.Text, options, _cancellationToken); completed++; }
                }
                if (!stopped) LogSqlScript(null, 0, location + (syntaxOnly ? ": syntax checked." : ": statement completed."));
            }
            catch (Exception error) when (error is CpfException or ClParseException)
            {
                var id = error is CpfException cpf ? cpf.MessageId : "IPC0005";
                var severity = error is CpfException native ? native.Severity : 30;
                highest = Math.Max(highest, severity); stopped |= severity > level;
                var message = location + ": " + error.Message;
                if (message.Length > 4096) message = message[..4096];
                diagnostics.Add(message); LogSqlScript(id, severity, message);
            }
        }
        var summary = process == "*SYN" ? $"RUNSQLSTM syntax checked {statements.Count} statements." : $"RUNSQLSTM completed {completed} statements; {affected} SQL rows affected.";
        return highest > level ? CommandResult.Error("SQL9010: RUNSQLSTM failed. " + string.Join(" ", diagnostics.Take(3)), "SQL9010") :
            CommandResult.Ok(summary + (diagnostics.Count == 0 ? "" : $" {diagnostics.Count} diagnostics within ERRLVL({level})."));
    }
    private SqlExecutionOptions SqlScriptOptions(SqlNaming naming, string collection) => new(naming,
        SearchLibraries("*CURLIB").First(), _job?.UserProfile ?? "QSECOFR", SearchLibraries("*LIBL").ToArray(),
        _job?.Ccsid ?? _system.Config.Ccsid, collection == "*NONE" ? null : collection);
    private void LogSqlScript(string? id, int severity, string text)
    {
        if (_job is not null) _system.Jobs.WriteLog(_job.Key, severity == 0 ? "INFO" : "DIAGNOSTIC", id, severity, text);
    }
}
