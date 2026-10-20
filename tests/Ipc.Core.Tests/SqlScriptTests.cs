using System.Text;
using Ipc.Cl.Commands;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class SqlScriptTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly ExecutionSession _session;
    public SqlScriptTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "SqlScriptsFixtureAdmin77");
        _files = new(_system.Connections, _system.Objects); _files.CreateSourceFile("QGPL", "QSQLSRC", sourceWidth: 240);
        _session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), CancellationToken.None);
    }
    private void Source(string text) => _files.SaveSourceMember("QGPL", "QSQLSRC", "SCRIPT", text, _files.ReadSourceMember("QGPL", "QSQLSRC", "SCRIPT").Revision);
    private CommandResult Run(string options = "") => _session.Execute("RUNSQLSTM SRCFILE(QGPL/QSQLSRC) SRCMBR(SCRIPT) COMMIT(*NONE) OPTION(*NOLIST) " + options);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private IReadOnlyList<Ipc.Core.Work.JobLogEntry> Log() => _system.Jobs.GetLog(_session.Job.Key, 10000);
    private object? Scalar(string sql)
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    public void Dispose() { _session.Dispose(); _system.Dispose(); }

    [Theory]
    [InlineData("*SYN")]
    [InlineData("*RUN")]
    public void SELECT_requires_a_query_interface_in_syntax_and_execution_modes(string process)
    {
        Source("SELECT ID FROM QGPL/MISSING;");
        Assert.Equal("SQL9010", Run("PROCESS(" + process + ")").MessageId);
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0104");
    }

    [Fact]
    public void Member_scripts_execute_DDL_and_mutations_in_order_through_shared_file_storage()
    {
        Source("""
            CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY, NAME CHAR(12));
            INSERT INTO QGPL/DATA VALUES(1,'first'),(2,'second');
            UPDATE QGPL/DATA SET ID=ID+10 WHERE ID=1;
            DELETE FROM QGPL/DATA WHERE ID=2;
            """);
        var result = Run(); Ok(result); Assert.Contains("4 statements; 4 SQL rows", result.Message);
        var row = Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA")); Assert.Equal(11L, row["ID"]); Assert.Equal("first", row["NAME"]);
        Assert.Equal(4, Log().Count(entry => entry.Text?.Contains("statement completed") == true));
    }

    [Fact]
    public void Semicolons_and_comment_markers_in_literals_do_not_split_statements_or_expose_catalog_SQL()
    {
        Source("""
            /* heading ; /* nested ; */ */
            CREATE TABLE QGPL/DATA(TEXT VARCHAR(100)); -- ; ignored
            INSERT INTO QGPL/DATA VALUES('a;--/*''b');
            INSERT INTO QGPL/DATA VALUES('x''); DELETE FROM sys_objects; --');
            """);
        Ok(Run()); var rows = _files.ReadAll("QGPL", "DATA", "DATA"); Assert.Equal(2, rows.Count);
        Assert.Equal("a;--/*'b", rows[0]["TEXT"]); Assert.True(_files.FileExists("QGPL", "QSQLSRC"));
    }

    [Fact]
    public void A_failure_preserves_prior_no_commit_work_stops_execution_and_checks_remaining_syntax()
    {
        Source("""
            CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY);
            INSERT INTO QGPL/DATA VALUES(1);
            INSERT INTO QGPL/DATA VALUES(1);
            INSERT INTO QGPL/DATA VALUES(2);
            UPDATE QGPL/DATA SET ID=;
            """);
        var result = Run(); Assert.True(result.IsError); Assert.Equal("SQL9010", result.MessageId);
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0803" && entry.Text!.Contains("SCRIPT):3:1"));
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0104" && entry.Text!.Contains("SCRIPT):5:1"));
    }

    [Theory]
    [InlineData(29, true, 1)]
    [InlineData(30, false, 2)]
    [InlineData(40, false, 2)]
    public void ERRLVL_uses_a_strict_severity_comparison_and_keeps_diagnostics(int level, bool failure, int rows)
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY);\nINSERT INTO QGPL/DATA VALUES(1);\nINSERT INTO QGPL/DATA VALUES(1);\nINSERT INTO QGPL/DATA VALUES(2);");
        var result = Run("ERRLVL(" + level + ")"); Assert.Equal(failure, result.IsError); Assert.Equal(rows, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0803" && entry.Severity == 30);
    }

    [Theory]
    [InlineData("CREATE TABLE QGPL/DATA(ID INTEGER)")]
    [InlineData("CREATE TABLE QGPL/DATA(ID INTEGER); INSERT INTO QGPL/DATA VALUES('unclosed);")]
    [InlineData("CREATE TABLE QGPL/DATA(ID INTEGER); /* unterminated")]
    public void Missing_terminators_or_unclosed_literals_comments_fail_with_SQL9010(string source)
    {
        Source(source); Assert.Equal("SQL9010", Run().MessageId);
        Assert.Equal(source.Contains(';'), _files.FileExists("QGPL", "DATA"));
    }

    [Fact]
    public void Syntax_only_checks_all_statements_without_creating_tables_or_running_CL()
    {
        Source("CL: ADDLIBLE QGPL;\nCREATE TABLE QGPL/DATA(ID INTEGER);\nINSERT INTO QGPL/DATA VALUES(1);\nUPDATE QGPL/DATA SET ID=;");
        Assert.Equal("SQL9010", Run("PROCESS(*SYN)").MessageId); Assert.False(_files.FileExists("QGPL", "DATA"));
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0104");
        Source("CREATE TABLE QGPL/DATA(ID INTEGER); INSERT INTO QGPL/DATA VALUES(1);");
        Ok(_session.Execute("RUNSQLSTM QGPL/QSQLSRC SCRIPT *CHG *SYS OPTION(*NOLIST) PROCESS(*SYN)"));
        Assert.False(_files.FileExists("QGPL", "DATA"));
    }

    [Fact]
    public void Mixed_CL_changes_the_library_environment_used_by_following_SQL()
    {
        _system.Libraries.CreateLibrary("SQLLIB");
        Source("CL: CHGCURLIB CURLIB(SQLLIB);\nCREATE TABLE DATA(ID INTEGER);\nINSERT INTO DATA VALUES(7);");
        Ok(Run()); Assert.Equal(7L, Assert.Single(_files.ReadAll("SQLLIB", "DATA", "DATA"))["ID"]);
    }

    [Theory]
    [InlineData(80, true)]
    [InlineData(81, false)]
    public void Member_margins_default_to_80_and_are_applied_before_statement_parsing(int margin, bool failure)
    {
        const string statement = "CREATE TABLE QGPL/DATA(ID INTEGER)";
        Source(statement.PadRight(80) + ";");
        var result = Run(margin == 80 ? "" : "MARGINS(81)"); Assert.Equal(failure, result.IsError); Assert.Equal(!failure, _files.FileExists("QGPL", "DATA"));
    }

    [Fact]
    public void Source_sequence_and_date_fields_are_excluded_and_deleted_records_are_skipped()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);\nINSERT INTO QGPL/DATA VALUES(1);\nINSERT INTO QGPL/DATA VALUES(2);");
        Scalar("DELETE FROM \"QGPL.QSQLSRC.SCRIPT\" WHERE rowid=2");
        Ok(Run()); Assert.Equal(2L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }

    [Theory]
    [InlineData(37, "café")]
    [InlineData(1208, "東京")]
    public void Ordinary_character_PF_records_can_supply_SQL_source_in_their_declared_CCSID(int code, string value)
    {
        var options = new SqlExecutionOptions(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"], code);
        _files.ExecuteSql("CREATE TABLE QGPL.SOURCE(TEXT CHAR(200))", options);
        _files.Insert("QGPL", "SOURCE", "SOURCE", "SOURCE", new Dictionary<string, object?> { ["TEXT"] = "CREATE TABLE QGPL/DATA(TEXT CHAR(20) CCSID " + code + ");" });
        _files.Insert("QGPL", "SOURCE", "SOURCE", "SOURCE", new Dictionary<string, object?> { ["TEXT"] = "INSERT INTO QGPL/DATA VALUES('" + value + "');" });
        Ok(_session.Execute("RUNSQLSTM QGPL/SOURCE SOURCE *NONE *SYS OPTION(*NOLIST) MARGINS(200)"));
        Assert.Equal(value, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["TEXT"]);
    }

    [Fact]
    public void A_UTF8_stream_ignores_member_margins_and_removes_its_BOM()
    {
        var path = Path.Combine(Path.GetTempPath(), "ipc-sql-script-" + Guid.NewGuid().ToString("N") + ".sql");
        try
        {
            File.WriteAllText(path, "CREATE TABLE QGPL/DATA(ID INTEGER);".PadRight(160) + "INSERT INTO QGPL/DATA VALUES(1);", new UTF8Encoding(true));
            Ok(_session.Execute("RUNSQLSTM SRCSTMF('" + path + "') COMMIT(*NONE) OPTION(*NOLIST) MARGINS(1)"));
            Assert.Equal(1, _files.RowCount("QGPL", "DATA", "DATA"));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("COMMIT(*CHG)")]
    [InlineData("OPTION(*LIST)")]
    [InlineData("ERRLVL(41)")]
    [InlineData("MARGINS(0)")]
    [InlineData("PROCESS(*BAD)")]
    [InlineData("SECLVLTXT(*YES)")]
    [InlineData("DFTRDBCOL(*BAD)")]
    [InlineData("DECRESULT(63 63 0)")]
    public void Unsupported_or_invalid_command_options_reject_before_effects(string option)
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);");
        var command = "RUNSQLSTM SRCFILE(QGPL/QSQLSRC) SRCMBR(SCRIPT) COMMIT(*NONE) OPTION(*NOLIST)";
        var keyword = option[..option.IndexOf('(')];
        if (keyword is "COMMIT" or "OPTION") command = command.Replace(keyword == "COMMIT" ? "COMMIT(*NONE)" : "OPTION(*NOLIST)", option);
        else command += " " + option;
        Assert.True(_session.Execute(command).IsError); Assert.False(_files.FileExists("QGPL", "DATA"));
    }

    [Fact]
    public void Source_selection_requires_one_complete_member_or_one_stream()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);");
        foreach (var parameters in new[] { "", "SRCFILE(QGPL/QSQLSRC)", "SRCMBR(SCRIPT)", "SRCFILE(QGPL/QSQLSRC) SRCMBR(SCRIPT) SRCSTMF('/tmp/nope')" })
            Assert.True(_session.Execute("RUNSQLSTM " + parameters + " COMMIT(*NONE) OPTION(*NOLIST)").IsError);
        Assert.False(_files.FileExists("QGPL", "DATA"));
    }

    [Fact]
    public void Default_collection_and_SQL_naming_apply_to_each_statement()
    {
        Source("CREATE TABLE DATA(ID INTEGER); INSERT INTO DATA VALUES(3);"); Ok(Run("NAMING(*SQL) DFTRDBCOL(QGPL)"));
        Assert.Equal(3L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }

    [Fact]
    public void SQL9010_can_be_monitored_from_a_compiled_CL_caller()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER); INSERT INTO QGPL/DATA(BAD) VALUES(1);");
        _files.SaveSourceMember("QGPL", "QSQLSRC", "APP", """
            PGM
            RUNSQLSTM SRCFILE(QGPL/QSQLSRC) SRCMBR(SCRIPT) COMMIT(*NONE) OPTION(*NOLIST)
            MONMSG MSGID(SQL9010) EXEC(SNDPGMMSG MSG('SCRIPT ERROR MONITORED'))
            ENDPGM
            """, null);
        Ok(_session.Execute("CRTCLPGM PGM(QGPL/APP) SRCFILE(QGPL/QSQLSRC)"));
        var result = _session.Execute("CALL QGPL/APP"); Ok(result); Assert.Contains("SCRIPT ERROR MONITORED", result.Message);
        Assert.True(_files.FileExists("QGPL", "DATA")); Assert.Equal(0, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Theory]
    [InlineData("-- comments; only\n/* nested /* more */ end */")]
    [InlineData("")]
    [InlineData(" ; ; \n")]
    public void Empty_and_comment_only_scripts_complete_without_effects(string text)
    { Source(text); Ok(Run()); Assert.False(_files.FileExists("QGPL", "DATA")); }

    [Fact]
    public void A_script_statement_can_exceed_RUNSQLs_5000_byte_limit()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);\nINSERT INTO QGPL/DATA VALUES\n" + string.Join(",\n", Enumerable.Range(1, 1100).Select(value => "(" + value + ")")) + ";");
        Ok(Run()); Assert.Equal(1100, _files.RowCount("QGPL", "DATA", "DATA"));
    }

    [Fact]
    public void A_script_runs_from_its_initial_snapshot_even_when_a_CL_command_clears_its_source()
    {
        Source("CL: CLRPFM FILE(QGPL/QSQLSRC) MBR(SCRIPT);\nCREATE TABLE QGPL/DATA(ID INTEGER);\nINSERT INTO QGPL/DATA VALUES(7);");
        Ok(Run()); Assert.Equal(7L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.Equal(0, _files.RowCount("QGPL", "QSQLSRC", "SCRIPT"));
    }

    [Fact]
    public void Source_and_target_authority_are_checked_as_the_executing_user()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);");
        _system.Security.Profiles.Create(new() { Name = "SCRIPTUSR" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SCRIPTUSR"), "SqlScriptUserFixture78");
        _system.Security.Authority.Grant("QGPL", "QSQLSRC", ObjectType.File, "SCRIPTUSR", AuthorityBit.None);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("SCRIPTUSR"), CancellationToken.None);
        var command = "RUNSQLSTM QGPL/QSQLSRC SCRIPT *NONE *SYS OPTION(*NOLIST)";
        Assert.Equal("SQL9010", session.Execute(command).MessageId); Assert.False(_files.FileExists("QGPL", "DATA"));
        _system.Security.Authority.Grant("QGPL", "QSQLSRC", ObjectType.File, "SCRIPTUSR", Authorities.UseBits);
        Assert.Equal("SQL9010", session.Execute(command).MessageId); Assert.False(_files.FileExists("QGPL", "DATA"));
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "SCRIPTUSR", Authorities.UseBits | AuthorityBit.Add);
        Ok(session.Execute(command)); Assert.Equal("SCRIPTUSR", _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File).Owner);
    }

    [Fact]
    public void Host_stream_access_requires_the_existing_SERVICE_authority()
    {
        _system.Security.Profiles.Create(new() { Name = "SCRIPTUSR" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SCRIPTUSR"), "SqlScriptUserFixture78");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("SCRIPTUSR"), CancellationToken.None);
        var result = session.Execute("RUNSQLSTM SRCSTMF('/tmp/should-not-be-read.sql') COMMIT(*NONE) OPTION(*NOLIST)");
        Assert.Equal("SQL9010", result.MessageId); Assert.Contains("CPF9802", result.Message);
    }

    [Fact]
    public void Source_member_and_stream_parameters_are_published_in_prompt_metadata()
    {
        var metadata = _session.CommandMetadata("RUNSQLSTM");
        Assert.Equal(4, metadata.MaximumPositional);
        Assert.Contains(metadata.Parameters, parameter => parameter.Keyword == "SRCSTMF");
        Assert.Contains(metadata.Parameters, parameter => parameter.Keyword == "ERRLVL");
    }

    [Theory]
    [InlineData("cancel_requested=1", true)]
    [InlineData("execution_state='Succeeded'", false)]
    public void Unique_key_publication_rechecks_the_job_on_its_own_connection_and_rolls_back_failed_publication(string change, bool cancelled)
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY);\nINSERT INTO QGPL/DATA VALUES(1);"); Ok(Run());
        Scalar("CREATE TRIGGER invalidate_sql_job AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN UPDATE sys_jobs SET " + change + " WHERE number=" + _session.Job.Key.Number + "; END");
        if (cancelled) Assert.ThrowsAny<OperationCanceledException>(() => _session.Execute("RUNSQL SQL('UPDATE QGPL/DATA SET ID=ID+1') COMMIT(*NONE)"));
        else Assert.Equal("CPF1241", _session.Execute("RUNSQL SQL('UPDATE QGPL/DATA SET ID=ID+1') COMMIT(*NONE)").MessageId);
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.Equal("Running", Scalar("SELECT execution_state FROM sys_jobs WHERE number=" + _session.Job.Key.Number));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Script_line_quotas_cover_all_line_ending_styles(string newline)
    {
        Assert.Equal("SQL0904", Assert.Throws<CpfException>(() => SqlScript.Read(string.Concat(Enumerable.Repeat(newline, 10001)))).MessageId);
    }

    [Fact]
    public void Source_margin_truncation_never_silently_replaces_a_partial_UTF8_character()
    {
        var options = new SqlExecutionOptions(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"], 1208);
        _files.ExecuteSql("CREATE TABLE QGPL.SOURCE(TEXT CHAR(10))", options);
        _files.Insert("QGPL", "SOURCE", "SOURCE", "SOURCE", new Dictionary<string, object?> { ["TEXT"] = "é" });
        Assert.Equal("SQL9010", _session.Execute("RUNSQLSTM QGPL/SOURCE SOURCE *NONE *SYS OPTION(*NOLIST) MARGINS(1)").MessageId);
        Assert.Contains(Log(), entry => entry.MessageId == "SQL0332");
    }

    [Fact]
    public void Final_profile_validation_after_unique_index_changes_uses_the_same_connection_and_rolls_back()
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER PRIMARY KEY);\nINSERT INTO QGPL/DATA VALUES(1);"); Ok(Run());
        Scalar("CREATE TRIGGER revoke_sql_profile AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN UPDATE sys_profiles SET status='Disabled' WHERE name='QSECOFR'; END");
        Assert.Equal("SQL0551", _session.Execute("RUNSQL SQL('UPDATE QGPL/DATA SET ID=ID+1') COMMIT(*NONE)").MessageId);
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
        Assert.Equal("Enabled", Scalar("SELECT status FROM sys_profiles WHERE name='QSECOFR'"));
    }

    [Theory]
    [InlineData("CREATE TRIGGER T AFTER INSERT ON QGPL/DATA BEGIN INSERT INTO QGPL/DATA VALUES(2); DELETE FROM QGPL/DATA; END;")]
    [InlineData("BEGIN INSERT INTO QGPL/DATA VALUES(2); DELETE FROM QGPL/DATA; END;")]
    [InlineData("CREATE OR REPLACE PROCEDURE P() BEGIN DELETE FROM QGPL/DATA; END;")]
    [InlineData("MYBLOCK: BEGIN DELETE FROM QGPL/DATA; END;")]
    [InlineData("\"MY BLOCK\": BEGIN DELETE FROM QGPL/DATA; END;")]
    [InlineData("IF 1=1 THEN DELETE FROM QGPL/DATA; END IF;")]
    public void Unsupported_procedural_source_never_executes_body_fragments_even_at_ERRLVL_40(string routine)
    {
        Source("CREATE TABLE QGPL/DATA(ID INTEGER);\nINSERT INTO QGPL/DATA VALUES(1);"); Ok(Run());
        Source("INSERT INTO QGPL/DATA VALUES(3);\n" + routine.Replace("; ", ";\n"));
        Assert.Equal("SQL9010", Run("ERRLVL(40)").MessageId);
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
    }

    [Fact]
    public async Task Cancelling_a_script_capacity_wait_preserves_completed_statements_and_cleans_the_wait()
    {
        _files.ExecuteSql("CREATE TABLE QGPL.DATA(ID INTEGER)", new(SqlNaming.Sql, "QGPL", "QGPL", ["QGPL"]));
        _files.ChangePhysicalMemberLimit("QGPL", "DATA", null, memberSize: new(1, 1, 1, false));
        Source("INSERT INTO QGPL/DATA VALUES(1);\nINSERT INTO QGPL/DATA VALUES(2),(3);\nINSERT INTO QGPL/DATA VALUES(4);");
        using var cancel = new CancellationTokenSource();
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), cancel.Token);
        new Ipc.Services.Messages.InquiryReplyPolicy(_system.Connections).Set(session.Job.Key, "*RQD");
        var work = Task.Factory.StartNew(() => Record.Exception(() => session.Execute("RUNSQLSTM QGPL/QSQLSRC SCRIPT *NONE *SYS OPTION(*NOLIST)")),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            var messages = new Ipc.Services.Messages.MessageQueueStore(_system.Connections);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!messages.List(new("QSYS", "QSYSOPR")).Any(message => message.MessageId == "CPA5305" && !message.Replied))
            { Assert.True(DateTime.UtcNow < deadline, "Expected script capacity inquiry."); await Task.Delay(20); }
            Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
            var other = _system.Jobs.CreateInteractive("QSECOFR");
            using (new Ipc.Services.Work.JobLockStore(_system.Connections).Acquire(other.Key,
                new("QGPL", "DATA", ObjectType.File, "DATA", 1), Ipc.Services.Work.JobLockMode.SharedRead, TimeSpan.Zero)) { }
            cancel.Cancel(); Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "DATA", "DATA"))["ID"]);
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_size_waits"));
        }
        finally { cancel.Cancel(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
