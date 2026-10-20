using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services.Commands;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Session.Sql;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteQueryManagement(CommandCall call)
    {
        try { return ExecuteQueryManagementCore(call); }
        catch (CpfException error) when (error.MessageId.StartsWith("SQL", StringComparison.Ordinal))
        {
            var diagnostic = SqlDiagnostics.Enrich(error);
            var message = diagnostic.Message + " SQLSTATE=" + diagnostic.SqlState;
            if (_job is not null) _system.Jobs.WriteLog(_job.Key, "DIAGNOSTIC", diagnostic.MessageId, diagnostic.Severity, message);
            throw new CpfException("QWM2701", call.Name + " failed. " + message);
        }
    }
    private CommandResult ExecuteQueryManagementCore(CommandCall call)
    {
        string Argument(string keyword, int position = -1, string fallback = "")
        {
            var value = call.GetOption(keyword);
            if (position >= 0 && call.Positional.Count > position)
            { if (value is not null) throw new CpfException("IPC0003", "Repeated " + keyword + "."); value = call.Positional[position]; }
            return CommandParser.Unquote(value ?? fallback);
        }
        string Choice(string keyword, string fallback, params string[] supported)
        {
            var value = Argument(keyword, fallback: fallback).ToUpperInvariant();
            if (!supported.Contains(value)) throw new CpfException("QWM2701", call.Name + " " + keyword + "(" + value + ") is invalid or remains unfinished.");
            return value;
        }
        var operation = call.Name.ToUpperInvariant(); var query = Argument("QMQRY", 0).ToUpperInvariant();
        if (query.Length == 0) throw new CpfException("IPC0003", "QMQRY is required.");
        var store = new QueryManagementStore(_system.Connections);
        if (operation == "CRTQMQRY")
        {
            var (library, name) = SplitQualified(query, "*CURLIB");
            if (library == "*CURLIB") library = SearchLibraries(library).First();
            if (!ObjectName.IsValid(library) || !ObjectName.IsValid(name)) throw new CpfException("IPC0003", "Invalid query creation name.");
            var sort = Choice("SRTSEQ", "*SRC", "*SRC", "*HEX", "*JOB", "*JOBRUN");
            var language = Choice("LANGID", "*SRC", "*SRC", "*JOB", "*JOBRUN");
            var replace = Choice("REPLACE", "*YES", "*YES", "*NO") == "*YES";
            var sourceFile = ResolveFileReference(Argument("SRCFILE", 1, "QQMQRYSRC").ToUpperInvariant());
            var member = Argument("SRCMBR", 2, "*QMQRY").ToUpperInvariant(); if (member == "*QMQRY") member = name;
            if (!ObjectName.IsValid(member)) throw new CpfException("IPC0003", "Invalid query source member.");
            var definition = _files.GetDefinition(sourceFile.Library, sourceFile.Name) ?? throw new CpfException("QWM2705", "Query source file was not found.");
            if (definition.Attribute != Ipc.Db.Definitions.FileAttribute.Source) throw new CpfException("QWM2705", "CRTQMQRY requires a source physical file.");
            var ccsid = definition.PrimaryFormat.Find("SRCDTA")!.Ccsid;
            var source = _files.ReadSqlSource(sourceFile.Library, sourceFile.Name, member, 79, ccsid, _cancellationToken);
            var text = Argument("TEXT", fallback: "*SRCMBRTXT");
            // The source-member catalog currently has no descriptive text column.
            if (text.ToUpperInvariant() is "*SRCMBRTXT" or "*BLANK") text = "";
            store.Create(library, name, source, ccsid, text, Argument("AUT", fallback: "*LIBCRTAUT").ToUpperInvariant(), replace,
                sort == "*SRC" ? "*JOBRUN" : sort, language == "*SRC" ? "*JOBRUN" : language, _cancellationToken);
            return CommandResult.Ok("Query " + library + "/" + name + " created.");
        }
        var (search, objectName) = SplitQualified(query, "*LIBL");
        if (!ObjectName.IsValid(objectName) || search is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(search))
            throw new CpfException("IPC0003", "This query operation requires an exact object name in a library, *LIBL or *CURLIB.");
        if (operation is "STRQMQRY" or "RTVQMQRY") _ = Choice("ALWQRYDFN", "*NO", "*NO");
        var naming = operation == "STRQMQRY" ? Choice("NAMING", "*SYS", "*SYS", "*SQL", "*SAA") : "*SYS";
        if (naming != "*SYS" && search == "*LIBL") throw new CpfException("QWM2707", "SQL naming requires an explicit query library or *CURLIB; *LIBL is not allowed.");
        var required = operation == "DLTQMQRY" ? AuthorityBit.ObjectExist : Authorities.UseBits;
        var libraryFound = SearchLibraries(search).FirstOrDefault(candidate => store.Find(candidate, objectName, required) is not null)
            ?? throw new CpfException("CPF9801", "Query management query not found: " + query);
        if (operation == "DLTQMQRY")
        {
            _system.Objects.Delete(libraryFound, objectName, ObjectType.QueryManagementQuery);
            return CommandResult.Ok("Query " + libraryFound + "/" + objectName + " deleted.");
        }
        var saved = store.Read(libraryFound, objectName, forExecution: operation == "STRQMQRY");
        if (operation == "RTVQMQRY")
        {
            var file = Argument("SRCFILE", 1); if (file.Length == 0) throw new CpfException("IPC0003", "SRCFILE is required.");
            var target = ResolveFileReference(file.ToUpperInvariant());
            var member = Argument("SRCMBR", 2, "*QMQRY").ToUpperInvariant(); if (member == "*QMQRY") member = objectName;
            if (!ObjectName.IsValid(member)) throw new CpfException("IPC0003", "Invalid retrieval source member.");
            var previous = _files.ReadSourceMember(target.Library, target.Name, member);
            _files.SaveSourceMember(target.Library, target.Name, member, saved, previous.Revision);
            return CommandResult.Ok("Query source retrieved to " + target.Library + "/" + target.Name + "(" + member + ").");
        }
        var output = Argument("OUTPUT", 1, "*").ToUpperInvariant();
        if (output is not ("*" or "*OUTFILE")) throw new CpfException("QWM2701", "Query printer output remains unfinished or OUTPUT is invalid.");
        _ = Choice("QMFORM", "*SYSDFT", "*SYSDFT"); _ = Choice("RDB", "*NONE", "*NONE", "*CURRENT");
        _ = Choice("RDBCNNMTH", "*DUW", "*DUW", "*RUW"); _ = Choice("USER", "*CURRENT", "*CURRENT"); _ = Choice("PASSWORD", "*NONE", "*NONE");
        _ = Choice("DATETIME", "*YES", "*YES", "*NO"); _ = Choice("PAGNBR", "*YES", "*YES", "*NO");
        var outputName = Argument("OUTFILE").ToUpperInvariant();
        var outputMember = CommandParser.Tokenize(Argument("OUTMBR", fallback: "*FIRST *REPLACE")).Select(value => CommandParser.Unquote(value).ToUpperInvariant()).ToArray();
        if (output == "*OUTFILE" && outputName.Length == 0) throw new CpfException("IPC0003", "OUTPUT(*OUTFILE) requires OUTFILE.");
        if (output != "*OUTFILE" && call.GetOption("OUTFILE", "OUTMBR") is not null) throw new CpfException("IPC0003", "OUTFILE/OUTMBR require OUTPUT(*OUTFILE).");
        if (outputMember.Length is < 1 or > 2 || outputMember[0] != "*FIRST" || outputMember.Length == 2 && outputMember[1] is not ("*REPLACE" or "*ADD"))
            throw new CpfException("IPC0003", "OUTMBR requires *FIRST and *REPLACE or *ADD.");
        var (outputLibrary, outputFile) = SplitQualified(outputName, "*CURLIB");
        if (output == "*OUTFILE")
        {
            if (!ObjectName.IsValid(outputFile) || outputLibrary is not ("*CURLIB" or "*LIBL") && !ObjectName.IsValid(outputLibrary))
                throw new CpfException("IPC0003", "Invalid output file name.");
            if (naming != "*SYS" && outputLibrary == "*LIBL") throw new CpfException("QWM2707", "*LIBL is not allowed for SQL-named output.");
        }
        var values = QuerySetVariables(call);
        foreach (var variable in QueryManagementSql.Variables(saved))
        {
            if (values.ContainsKey(variable)) continue;
            if (_job?.Type != JobType.Interactive || _programTerminal is null)
                throw new CpfException("QWM2701", "No value was supplied for query variable " + variable + "; interactive prompting is unavailable.");
            var prompt = new QueryVariableScreen(variable);
            using var display = new ProgramDisplay(prompt.Buffer, prompt.Handle, new("QSYS", "STRQMQRY"), Validate, "QueryVariable");
            var response = _programTerminal.Read(display, _cancellationToken); Validate();
            var value = ((string)response.Values["VALUE"]!).TrimEnd();
            if (response.Aid != AidKey.Enter || value.Length == 0) throw new CpfException("QWM2701", "Query variable prompting was canceled.");
            values.Add(variable, value == "*BLANK" ? " " : value);
        }
        var options = SqlScriptOptions(naming == "*SYS" ? SqlNaming.System : SqlNaming.Sql, "*NONE");
        var sql = QueryManagementSql.Substitute(saved, values, options.Ccsid);
        // The shared AST enforces one statement and isolates every database operation
        // from host/catalog SQL, including user-supplied SQL fragments in SETVAR.
        var statement = SqlStatementParser.Parse(sql, options); Validate();
        if (statement is not SqlSelect)
        {
            var affected = _files.ExecuteSql(sql, options, _cancellationToken);
            return CommandResult.Ok("Query completed; " + affected + " rows affected.");
        }
        if (output == "*OUTFILE")
        {
            var (library, name) = (outputLibrary, outputFile);
            if (library == "*CURLIB") library = SearchLibraries(library).First();
            else if (library == "*LIBL") library = SearchLibraries(library).FirstOrDefault(candidate =>
                new Ipc.Services.Sqlite.SqliteObjectStore(_system.Connections).GetForAuthorization(candidate, name, ObjectType.File) is not null)
                ?? SearchLibraries("*CURLIB").First();
            var member = "*FIRST"; var overridden = _job is null ? null : EnvironmentForJob().Resolve(name);
            if (overridden is not null)
            {
                // The environment service only installs overrides to existing files/members.
                library = overridden.Library; name = overridden.Name; member = overridden.Member;
            }
            var savedOutput = _files.WriteSqlQueryFile(sql, options, new(library, name), member,
                replace: outputMember.Length == 1 || outputMember[1] == "*REPLACE", cancellationToken: _cancellationToken, validateQuery: Validate);
            if (_job is not null) foreach (var warning in savedOutput.Diagnostics)
                _system.Jobs.WriteLog(_job.Key, "DIAGNOSTIC", warning.MessageId, 10,
                    warning.Message + " SQLSTATE=" + warning.SqlState + " SQLCODE=" + warning.SqlCode + "; occurrences=" + warning.Occurrences);
            return CommandResult.Ok("Query completed; " + savedOutput.Rows + " rows written to " + library + "/" + name + "(" + savedOutput.Member + ").");
        }
        if (_job?.Type != JobType.Interactive || _programTerminal is null)
            throw new CpfException("QWM2701", "Query results require an interactive terminal or the unfinished printer output service.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, _system.Connections.ShutdownToken); var token = lifetime.Token;
        var settings = new InteractiveSqlSettings(); var screen = new InteractiveSqlScreen(settings, [], "", queryOnly: true);
        var formatter = new InteractiveSqlFormatter(settings, _system.SystemValues.Get(SystemValueNames.DateFormat).Value,
            _system.SystemValues.Get(SystemValueNames.DateSeparator).Value, _system.SystemValues.Get(SystemValueNames.TimeSeparator).Value,
            _system.SystemValues.Get(SystemValueNames.DecimalFormat).Value.Trim().Length != 0);
        InteractiveSqlResults? results = null;
        try
        {
            results = Open(); screen.ShowData(results.Read(0, screen.PageSize));
            while (true)
            {
                using var display = new ProgramDisplay(screen.Buffer, screen.Handle, new("QSYS", "STRQMQRY"), ValidateResults, "QueryData");
                var response = _programTerminal.Read(display, token); ValidateResults();
                var action = response.Values.GetValueOrDefault("ACTION") as string;
                if (action == "CLOSE") return CommandResult.Ok("Query completed.");
                if (action == "REFRESH") { results.Dispose(); results = Open(); }
                var first = action == "PAGE" ? Convert.ToInt64(response.Values["FIRST"], System.Globalization.CultureInfo.InvariantCulture) : screen.First;
                screen.ShowData(results.Read(first, screen.PageSize));
            }
        }
        finally { results?.Dispose(); }
        InteractiveSqlResults Open() => new(_system.Connections, _files, sql, options, formatter, token);
        void ValidateResults() { Validate(); results?.CheckAccess(); }
        void Validate()
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (store.Read(libraryFound, objectName, forExecution: true) != saved) throw new CpfException("QWM2701", "Query definition changed while it was running.");
            if (new CommandDefinitionStore(_system.Connections).Load("QSYS", "STRQMQRY").Builtin != "STRQMQRY")
                throw new CpfException("CPF9802", "STRQMQRY command definition changed.");
        }
    }
    private static Dictionary<string, string> QuerySetVariables(CommandCall call)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal); var entries = call.Split("SETVAR");
        if (entries.Count > 50) throw new CpfException("IPC0003", "SETVAR accepts at most 50 variables.");
        foreach (var entry in entries)
        {
            if (!entry.StartsWith('(') || !entry.EndsWith(')')) throw new CpfException("IPC0003", "SETVAR requires (name value) pairs.");
            var pair = CommandParser.Tokenize(entry[1..^1]);
            if (pair.Count != 2) throw new CpfException("IPC0003", "SETVAR requires (name value) pairs.");
            var name = CommandParser.Unquote(pair[0]).ToUpperInvariant(); var value = CommandParser.Unquote(pair[1]);
            QueryManagementSql.RequireName(name);
            if (value.Length > 55 || name.StartsWith("DSQ", StringComparison.Ordinal) || !result.TryAdd(name, value))
                throw new CpfException("IPC0003", "Invalid, repeated or reserved query variable, or value longer than 55 characters.");
        }
        return result;
    }
}
