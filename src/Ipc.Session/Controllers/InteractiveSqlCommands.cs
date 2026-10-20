using System.Globalization;
using System.Text;
using System.Text.Json;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Core.Work;
using Ipc.Db.Sql;
using Ipc.Services.Commands;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Ipc.Session;
using Ipc.Session.Sql;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteStartSql(CommandCall call)
    {
        if(_job?.Type!=JobType.Interactive || _programTerminal is null)throw new CpfException("SQL6141","STRSQL requires an interactive terminal.");
        var requested=InteractiveSqlSettings.FromCall(call);var history=new SqlSessionHistoryStore(_system.Connections);var saved=history.Load();
        InteractiveSqlSettings settings;
        try { settings=saved is null ? requested : JsonSerializer.Deserialize<InteractiveSqlSettings>(saved.Settings) ?? throw new JsonException(); }
        catch(JsonException) { throw new CpfException("SQL0901","Saved interactive SQL settings are invalid."); }
        settings.RequireSupported();
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken,_system.Connections.ShutdownToken);var token=lifetime.Token;
        var jobDateFormat=_system.SystemValues.Get(SystemValueNames.DateFormat).Value;
        var jobDateSeparator=_system.SystemValues.Get(SystemValueNames.DateSeparator).Value;
        var jobTimeSeparator=_system.SystemValues.Get(SystemValueNames.TimeSeparator).Value;
        var jobDecimalComma=_system.SystemValues.Get(SystemValueNames.DecimalFormat).Value.Trim().Length!=0;
        var screen=new InteractiveSqlScreen(settings,saved?.Statements ?? [],saved?.Draft ?? "");
        if(saved is not null)screen.Error("Saved session restored; STRSQL parameters were ignored.");
        InteractiveSqlResults? results=null;string? selected=null;var first=0L;
        try
        {
            while(true)
            {
                token.ThrowIfCancellationRequested();
                using var display=new ProgramDisplay(screen.Buffer,screen.Handle,new("QSYS","STRSQL"),Validate,screen.State);
                var response=_programTerminal.Read(display,token);Validate();
                var action=response.Values.GetValueOrDefault("ACTION") as string ?? throw new CpfException("SQL0901","Incomplete interactive SQL input.");
                try
                {
                    using var statementLocks=new JobLockStore(_system.Connections).EnterCommand(_job.Key,token);
                    if(action is "SAVE" or "DISCARD")
                    {
                        results?.Dispose();results=null;
                        if(action=="SAVE")history.Save(JsonSerializer.Serialize(screen.Settings),screen.History,screen.Draft,saved?.Revision);
                        else
                        {
                            try { history.Delete(saved?.Revision); }
                            catch(CpfException error) when(error.MessageId=="IPC0130") { return CommandResult.Ok("SQL session ended; a newer saved session was retained."); }
                        }
                        return CommandResult.Ok(action=="SAVE" ? "SQL session saved." : "SQL session ended without saving.");
                    }
                    if(action=="SOURCE")
                    {
                        results?.Dispose();results=null;
                        var target=ResolveFileReference(((string)response.Values["SRCFILE"]!).Trim().ToUpperInvariant());
                        var member=((string)response.Values["SRCMBR"]!).Trim().ToUpperInvariant();if(!ObjectName.IsValid(member))throw new CpfException("IPC0003","Invalid source member.");
                        var source=string.Join('\n',screen.History.Select(text=>text.TrimEnd().EndsWith(';') ? text.TrimEnd() : text.TrimEnd()+"\n;"));
                        var snapshot=_files.ReadSourceMember(target.Library,target.Name,member);
                        _files.SaveSourceMember(target.Library,target.Name,member,source,snapshot.Revision);
                        try { history.Delete(saved?.Revision); }
                        catch(CpfException error) { throw new CpfException(error.MessageId,"SQL source was saved; "+error.Message); }
                        return CommandResult.Ok("SQL session saved in "+target.Library+"/"+target.Name+"("+member+").");
                    }
                    if(action=="SETTINGS")
                    {
                        string Value(string name)=>((string)response.Values[name]!).Trim().ToUpperInvariant();
                        var changed=screen.Settings with { Naming=Value("NAMING"),Commit=Value("COMMIT"),Process=Value("PROCESS"),Refresh=Value("REFRESH"),CopyData=Value("ALWCPYDTA"),DateFormat=Value("DATFMT"),DateSeparator=Value("DATSEP"),TimeFormat=Value("TIMFMT"),TimeSeparator=Value("TIMSEP"),DecimalPoint=Value("DECPNT") };
                        changed.RequireSupported();screen.ChangeSettings(changed);continue;
                    }
                    if(action=="CLOSE") { results?.Dispose();results=null;screen.CloseData();continue; }
                    if(action=="RUN")
                    {
                        var text=(string)response.Values["SQL"]!;if(string.IsNullOrWhiteSpace(text)) { screen.Error("Enter a SQL statement.");continue; }
                        RequireSqlInput(text);results?.Dispose();results=null;
                        var options=Options();var parsed=SqlStatementParser.Parse(text,options);
                        if(screen.Settings.Process=="*SYN") { screen.Completed(text,"SQL statement syntax checked.");continue; }
                        if(screen.Settings.Process=="*VLD") { _files.ValidateSql(text,options,token);screen.Completed(text,"SQL statement validated; no statement was run.");continue; }
                        if(parsed is SqlSelect)
                        {
                            selected=text;first=0;results=OpenResults(text,options);var page=results.Read(0,screen.PageSize);
                            screen.Remember(text,"SELECT statement run complete.");screen.ClearEntry();screen.ShowData(page);
                        }
                        else
                        {
                            var count=_files.ExecuteSql(text,options,token);var message="SQL statement completed; "+count+" rows affected.";
                            screen.Completed(text,message);Log(null,0,message);
                        }
                    }
                    else if(action is "PAGE" or "REFRESH")
                    {
                        first=action=="PAGE" ? Convert.ToInt64(response.Values["FIRST"],CultureInfo.InvariantCulture) : screen.First;
                        if(selected is null)throw new CpfException("SQL0901","No query is active.");
                        if(action=="REFRESH" || screen.Settings.Refresh=="*ALWAYS") { results?.Dispose();results=OpenResults(selected,Options()); }
                        screen.ShowData(results!.Read(first,screen.PageSize));
                    }
                }
                catch(CpfException error)
                {
                    var diagnostic=SqlDiagnostics.Enrich(error);var message=diagnostic.MessageId+" SQLSTATE="+diagnostic.SqlState+(diagnostic.SqlCode is { } code ? " SQLCODE="+code : "")+": "+diagnostic.Message[(diagnostic.MessageId.Length+2)..];
                    if(action is "RUN" or "PAGE" or "REFRESH")
                    {
                        results?.Dispose();results=null;
                        if(action=="RUN")screen.Completed((string)response.Values["SQL"]!,message,clear:false);
                        else { screen.CloseData();screen.Error(message); }
                    }
                    else screen.Error(message);
                    Log(diagnostic.MessageId,diagnostic.Severity,message);
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                { results?.Dispose();results=null;screen.CloseData();screen.Error("SQL0901: Interactive SQL storage could not be accessed.");Log("SQL0901",30,"Interactive SQL storage could not be accessed."); }
            }
        }
        finally { results?.Dispose(); }

        void Validate()
        {
            token.ThrowIfCancellationRequested();using var accessLocks=new JobLockStore(_system.Connections).EnterCommand(_job.Key,token);new JobDataAreaStore(_system.Connections).RequireOwner(_job.Key);
            var command=new CommandDefinitionStore(_system.Connections).Load("QSYS","STRSQL");
            if(command.Builtin!="STRSQL")throw new CpfException("CPF9802","STRSQL command definition changed.");
            results?.CheckAccess();
        }
        SqlExecutionOptions Options()
        {
            var value=screen.Settings;
            var date=value.DateFormat=="*JOB" ? jobDateFormat : value.DateFormat;
            var dateSeparator=value.DateSeparator=="*JOB" ? jobDateSeparator : value.DateSeparator=="*BLANK" ? " " : value.DateSeparator;
            var timeSeparator=value.TimeSeparator=="*JOB" ? jobTimeSeparator : value.TimeSeparator=="*BLANK" ? " " : value.TimeSeparator;
            var comma=value.DecimalPoint=="*COMMA" || value.DecimalPoint=="*JOB" && jobDecimalComma || value.DecimalPoint=="*SYSVAL" && _system.SystemValues.Get(SystemValueNames.DecimalFormat).Value.Trim().Length!=0;
            return new(value.SqlNaming,SearchLibraries("*CURLIB").First(),_job.UserProfile ?? _job.Key.User,SearchLibraries("*LIBL").ToArray(),_job.Ccsid,Conversion:new(date,dateSeparator,value.TimeFormat,timeSeparator,comma));
        }
        InteractiveSqlResults OpenResults(string text,SqlExecutionOptions options)
        {
            var formatter=new InteractiveSqlFormatter(screen.Settings,jobDateFormat,jobDateSeparator,jobTimeSeparator,options.Conversion?.DecimalComma ?? jobDecimalComma);
            return new(_system.Connections,_files,text,options,formatter,token,screen.Settings.Refresh=="*FORWARD");
        }
        void RequireSqlInput(string text)
        {
            var encoding=(Encoding)Ipc.Core.Text.CodePage.FromCcsid(_job.Ccsid).Clone();encoding.EncoderFallback=EncoderFallback.ExceptionFallback;
            try { if(encoding.GetByteCount(text)>32768 || Encoding.UTF8.GetByteCount(text)>32768)throw new CpfException("SQL0904","SQL statement exceeds the interactive input limit."); }
            catch(EncoderFallbackException) { throw new CpfException("SQL0332","SQL statement cannot be represented in the job CCSID."); }
        }
        void Log(string? id,int severity,string text)=>_system.Jobs.WriteLog(_job.Key,severity==0 ? "INFO" : "DIAGNOSTIC",id,severity,text);
    }
}
