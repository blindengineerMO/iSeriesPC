using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Fact]
    public void File_success_refreshes_numeric_status_while_ERROR_tracks_only_E_operations()
    {
        Compile(UpdateFile(extra: "usropn") + """
            read(e) DATA;
            if %error and %status=1211 and %status(DATA)=1211;
              open(e) DATA;
              if not %error and %status=0;
                open(e) DATA;
                eval ID=999;
                if not %error or %status<>1215 or %status(DATA)<>1215;
                  sndpgmmsg 'BAD PROGRAM STATUS';
                  return;
                endif;
                read DATA;
                if %error and %status=0 and %status(DATA)=0;
                  read(e) DATA;
                  if not %error and %status=0 and %status(DATA)=0;
                    sndpgmmsg 'STATUS RESET ACCEPTED';
                  endif;
                endif;
              endif;
            endif;
            seton LR;
            """);
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "STATUS RESET ACCEPTED");
    }
    [Theory]
    [InlineData("read(e) DATA;", 1211)]
    [InlineData("close(e) DATA;", 1211)]
    [InlineData("update(e) ROW;", 1211)]
    [InlineData("unlock(e) DATA;", 1211)]
    public void Closed_file_operations_report_1211_without_modifying_fields(string operation, int status)
    {
        Compile(UpdateFile(extra: "usropn") + "eval TEXT='sentinel';\n" + operation + "\nif %error and %status=" + status +
            " and %status(DATA)=" + status + " and TEXT='sentinel';\nsndpgmmsg 'CLOSED';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "CLOSED"); Assert.Equal(3, Count);
    }
    [Fact]
    public void Successful_input_on_another_file_refreshes_global_status_without_clearing_the_first_file_or_ERROR()
    {
        Data("OTHER");
        Compile("**free\ndcl-f DATA keyed usropn;\ndcl-f OTHER keyed rename(ROW:OTHERROW) prefix(O_);\nread(e) DATA;\nread OTHER;\n" +
            "if %error and %status=0 and %status(DATA)=1211 and %status(OTHER)=0;\nsndpgmmsg 'STATUS INDEPENDENCE';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "STATUS INDEPENDENCE");
    }
    [Theory]
    [InlineData("read(e) DATA;")]
    [InlineData("read(ne) DATA;")]
    [InlineData("read(en) DATA;")]
    [InlineData("chain(ne) 20 DATA;")]
    [InlineData("setgt 20 DATA;\nreadp(en) DATA;")]
    [InlineData("setll 20 DATA;\nreade(ne) 20 DATA;")]
    [InlineData("setgt 20 DATA;\nreadpe(en) 20 DATA;")]
    public void N_and_E_combinations_have_distinct_lock_and_error_effects(string input)
    {
        var noLock = input.Contains("ne)") || input.Contains("en)");
        Compile(UpdateFile() + input + "\neval TEXT='changed';\nupdate(e) ROW;\n" +
            (noLock ? "if %error and %status=1221;" : "if not %error and %status=0;") + "\nsndpgmmsg 'MODE';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "MODE"); Assert.Equal(noLock ? 0 : 1, _files.ReadAll("QGPL", "DATA", "DATA").Count(r => ((string)r["TEXT"]!).TrimEnd() == "changed"));
    }
    [Fact]
    public void EOF_and_not_found_are_normal_numeric_statuses_and_do_not_set_ERROR()
    {
        Compile("**free\ndcl-f DATA keyed;\nchain(e) 999 DATA;\nif not %error and not %found(DATA) and %status=12;\n" +
            "setgt(e) 999 DATA;\nread(e) DATA;\nif not %error and %eof(DATA) and %status=11 and %status(DATA)=11;\n" +
            "sndpgmmsg 'NORMAL STATUS';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "NORMAL STATUS");
    }
    [Fact]
    public void Per_file_statuses_are_independent_and_record_format_operands_report_under_the_file_name()
    {
        Data("OTHER");
        Compile("**free\ndcl-f DATA keyed usropn;\ndcl-f OTHER keyed rename(ROW:OTHERROW) prefix(O_);\n" +
            "read(e) ROW;\nchain(e) 999 OTHERROW;\nif not %error and %status=12 and %status(OTHER)=12 and %status(DATA)=1211;\n" +
            "sndpgmmsg 'FILES';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "FILES");
    }
    [Theory]
    [InlineData("chain(e) 20 DATA;")]
    [InlineData("setll 20 DATA;\nread(e) DATA;")]
    [InlineData("setgt 20 DATA;\nreadp(e) DATA;")]
    [InlineData("setll 20 DATA;\nreade(e) 20 DATA;")]
    [InlineData("setgt 20 DATA;\nreadpe(e) 20 DATA;")]
    public void Record_contention_reports_1218_and_preserves_fields_without_an_update_target(string operation)
    {
        Compile(UpdateFile() + "eval TEXT='sentinel';\n" + operation + "\nif %error and %status=1218 and %status(DATA)=1218 and TEXT='sentinel';\n" +
            "update(e) ROW;\nif %error and %status=1221;\nsndpgmmsg 'LOCK STATUS';\nendif;\nendif;\nseton LR;");
        using var first = Session(); using var second = Session(); using var held = Hold(first);
        Ok(second.Execute("CALL QGPL/APP"), "LOCK STATUS"); Assert.Equal("first", Text(101));
    }
    [Fact]
    public void Object_allocation_failure_during_explicit_OPEN_reports_1217_rather_than_a_record_lock()
    {
        Compile(UpdateFile(extra: "usropn") + "open(e) DATA;\nif %error and %status=1217 and not %open(DATA);\nsndpgmmsg 'OPEN FAILURE';\nendif;\nseton LR;");
        using var owner = Session(); using var caller = Session();
        using var allocation = new Ipc.Services.Work.JobLockStore(_system.Connections).Acquire(owner.Job.Key, new("QGPL", "DATA", ObjectType.File), Ipc.Services.Work.JobLockMode.Exclusive, TimeSpan.Zero);
        Ok(caller.Execute("CALL QGPL/APP"), "OPEN FAILURE");
    }
    [Fact]
    public void A_duplicate_WRITE_reports_1021_and_a_later_E_WRITE_clears_the_error()
    {
        OutputView(unique: true);
        Compile(OutputFile() + "eval ID=104;\neval KEY=20;\nwrite(e) ROW;\nif %error and %status=1021;\n" +
            "eval KEY=45;\nwrite(e) ROW;\nif not %error and %status=0;\nsndpgmmsg 'DUPLICATE';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "DUPLICATE"); Assert.Equal(4, Count);
    }
    [Fact]
    public void Error_indicators_handle_native_file_errors_and_reset_without_setting_ERROR()
    {
        var program = Parse(UpdateFile(extra: "usropn") + "read DATA;\nif *in20 and not %error and %status=1211;\nsndpgmmsg 'INDICATOR';\nendif;\n" +
            "open DATA;\nread DATA;\nif not *in20 and not %error;\nsndpgmmsg 'CLEARED';\nendif;\nseton LR;");
        foreach (var read in program.MainStatements.Where(s => s.Opcode == Ipc.Rpg.Model.RpgOpcode.Read)) read.Indicator2 = 20;
        using var session = Session(); using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key); var messages = new List<string>();
        new RpgInterpreter(new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", SendMessage = messages.Add }).Run(program);
        Assert.Equal(new[] { "INDICATOR", "CLEARED" }, messages);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Referential_constraint_failures_report_1022_and_preserve_records(bool extended)
    {
        using (var connection = _system.Connections.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE UNIQUE INDEX status_parent ON \"QGPL.DATA.DATA\"(ID); CREATE TABLE status_child(ID INTEGER REFERENCES \"QGPL.DATA.DATA\"(ID)); INSERT INTO status_child VALUES(101)";
            command.ExecuteNonQuery();
        }
        var operation = extended ? "delete(e) 20 DATA;\nif %error and %status=1022;\nsndpgmmsg 'REFERENCE';\nendif;\n" :
            "monitor;\ndelete 20 DATA;\non-error 1022;\nif not %error and %status(DATA)=1022;\nsndpgmmsg 'REFERENCE';\nendif;\nendmon;\n";
        Compile(UpdateFile("*DELETE") + operation + "seton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "REFERENCE"); Assert.Equal(3, Count); Assert.Equal("first", Text(101));
    }
    [Fact]
    public void Numeric_and_error_status_survive_RETURN_in_a_retained_activation()
    {
        Compile(UpdateFile(extra: "usropn") + "dcl-s CALLS int(10);\neval CALLS=CALLS+1;\nif CALLS=1;\nread(e) DATA;\nreturn;\nendif;\n" +
            "if %error and %status=1211 and %status(DATA)=1211;\nsndpgmmsg 'RETAINED STATUS';\nendif;\nseton LR;", group: "STATUS");
        using var session = Session(); Assert.False(session.Execute("CALL QGPL/APP").IsError); Ok(session.Execute("CALL QGPL/APP"), "RETAINED STATUS");
    }
    [Fact]
    public void Failed_input_conversion_preserves_every_program_field_and_releases_the_update_target()
    {
        var format = new Ipc.Db.Definitions.RecordFormat { Name = "ROW", Fields = [
            new() { Name = "ID", Type = Ipc.Db.Definitions.FieldType.Binary, Length = 8 },
            new() { Name = "KEY", Type = Ipc.Db.Definitions.FieldType.Packed, Length = 9, Decimals = 2, Sequence = 1 },
            new() { Name = "TEXT", Type = Ipc.Db.Definitions.FieldType.Alpha, Length = 12, Ccsid = 1208 }] };
        format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "UTFDATA", new() { Name = "UTFDATA", Attribute = Ipc.Db.Definitions.FileAttribute.Physical,
            RecordLockWaitSeconds = 0, Formats = [format] }, "");
        _files.Insert("QGPL", "UTFDATA", "UTFDATA", "ROW", new Dictionary<string, object?> { ["ID"] = 104L, ["KEY"] = 5m, ["TEXT"] = "😀" });
        Compile(UpdateFile(extra: "extdesc('QGPL/UTFDATA') extfile(*extdesc)") + "eval ID=999;\neval KEY=99;\neval TEXT='sentinel';\nread(e) DATA;\n" +
            "if %error and %status=1299 and ID=999 and KEY=99 and TEXT='sentinel';\nupdate(e) ROW;\n" +
            "if %status=1221;\nsndpgmmsg 'BUFFER PRESERVED';\nendif;\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "BUFFER PRESERVED");
        Assert.Empty(new Ipc.Services.Work.JobLockStore(_system.Connections).Inspect(new("QGPL", "UTFDATA", ObjectType.File)));
    }
}
