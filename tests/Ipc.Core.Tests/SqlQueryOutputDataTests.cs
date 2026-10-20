using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlQueryOutputTests
{
    [Fact]
    public void Source_field_CCSIDs_survive_new_output_and_conversion_failure_keeps_the_published_prefix()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/UNICODE(TEXT VARCHAR(8) CCSID 1208)", Options);
        _files.ExecuteSql("INSERT INTO QGPL/UNICODE VALUES('first'),('😀')", Options with { Ccsid = 1208 });
        Save("SELECT TEXT FROM QGPL/UNICODE"); Assert.Equal(1208, _files.GetDefinition("QGPL", "OUT")!.PrimaryFormat.Fields[0].Ccsid);
        Assert.Equal("😀", Rows()[1]["TEXT"]);
        _files.ExecuteSql("CREATE TABLE QGPL/EBCDIC(TEXT VARCHAR(8) CCSID 37)", Options);
        var error = Assert.Throws<CpfException>(() => Save("SELECT TEXT FROM QGPL/UNICODE", "EBCDIC")); Assert.Equal("SQL0404", error.MessageId);
        Assert.Contains("1 rows were written", error.Message); Assert.Equal("first", Assert.Single(Rows("EBCDIC"))["TEXT"]);
    }
    [Fact]
    public void Output_uses_record_journal_hooks_and_does_not_journal_a_failed_insertion()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/OUT(ID INTEGER PRIMARY KEY)", Options); _files.ExecuteSql("INSERT INTO QGPL/OUT VALUES(99)", Options);
        var journal = new QualifiedName("QGPL", "OUTJRN"); var receiver = new QualifiedName("QGPL", "OUTRCV");
        _system.Journals.CreateReceiver(receiver); _system.Journals.Create(journal, receiver); _system.Journals.StartFile(new("QGPL", "OUT"), journal, "*BOTH");
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Save("SELECT 1 AS ID FROM QGPL/DATA")).MessageId);
        var records = _system.Journals.Read(journal).Where(entry => entry.Code == "R").ToArray();
        Assert.Equal(new[] { "DL", "PT" }, records.Select(entry => entry.Type)); Assert.All(records, entry => Assert.Equal(_session.Job.Key.ToString(), entry.Job));
    }
    [Fact]
    public void Pending_source_rows_can_be_exported_but_an_enlisted_target_is_rejected()
    {
        var receiver = new QualifiedName("QGPL", "PENDRCV"); var journal = new QualifiedName("QGPL", "PENDJRN");
        _system.Journals.CreateReceiver(receiver); _system.Journals.Create(journal, receiver); _system.Journals.StartFile(new("QGPL", "DATA"), journal, "*BOTH");
        using var identity = OperationIdentity.Enter("QSECOFR", _session.Job.Key);
        _system.RecordTransactions.Begin(_session.Job.Key); _system.RecordTransactions.Enlist(_session.Job.Key, new("QGPL", "DATA"));
        using (var change = _system.RecordTransactions.Open(_session.Job.Key)) { change.Execute("UPDATE \"QGPL.DATA.DATA\" SET NAME='pending' WHERE ID=1"); change.Complete(); }
        _files.WriteSqlQueryFile("SELECT ID,NAME FROM QGPL/DATA ORDER BY ID", Options, new("QGPL", "OUT")); Assert.Equal("pending", Rows()[0]["NAME"]);
        Assert.Throws<CpfException>(() => _files.WriteSqlQueryFile("SELECT ID,NAME FROM QGPL/DATA", Options, new("QGPL", "DATA")));
        _system.RecordTransactions.Rollback(_session.Job.Key); Assert.Equal("one", Rows("DATA")[0]["NAME"]); Assert.Equal("pending", Rows()[0]["NAME"]);
    }
    [Fact]
    public void Unaliased_calculations_use_SEL_while_an_explicit_numeric_alias_is_retained()
    {
        Save("SELECT ID+1,ID AS \"00001\",ID+2 FROM QGPL/DATA WHERE ID=1");
        Assert.Equal(new[] { "SEL", "00001", "SEL1" }, _files.GetDefinition("QGPL", "OUT")!.PrimaryFormat.Fields.Select(field => field.Name));
    }
    [Fact]
    public void Source_keys_constraints_and_defaults_are_not_propagated_into_the_answer_table()
    {
        _files.ExecuteSql("CREATE TABLE QGPL/KEYED(ID INTEGER PRIMARY KEY,NAME VARCHAR(5) DEFAULT 'x')", Options);
        _files.ExecuteSql("INSERT INTO QGPL/KEYED(ID) VALUES(1)", Options); Save("SELECT ID,NAME FROM QGPL/KEYED");
        var output = _files.GetDefinition("QGPL", "OUT")!; Assert.Empty(output.SqlTable!.Constraints);
        Assert.All(output.PrimaryFormat.Fields, field => { Assert.Equal(0, field.Sequence); Assert.Null(field.DefaultValue); });
        _files.ExecuteSql("INSERT INTO QGPL/OUT VALUES(1,'again')", Options); Assert.Equal(2, Rows().Count);
    }
    [Fact]
    public void Boolean_and_untyped_null_results_have_readable_local_physical_layouts()
    {
        Save("SELECT ID>1 AS FLAG,NULL AS EMPTY FROM QGPL/DATA ORDER BY ID");
        Assert.Equal(new[] { false, true, true }, Rows().Select(row => (bool)row["FLAG"]!)); Assert.All(Rows(), row => Assert.Null(row["EMPTY"]));
    }
    [Fact]
    public void Query_warnings_survive_spooling_and_are_written_to_the_command_job_log()
    {
        var saved = Save("SELECT CAST(NAME AS CHAR(1)) AS SHORT FROM QGPL/DATA");
        Assert.Contains(saved.Diagnostics, diagnostic => diagnostic.SqlState == "01004" && diagnostic.Occurrences == 2);
        Query("SELECT CAST(NAME AS CHAR(1)) AS SHORT FROM QGPL/DATA"); Ok(_session.Execute("STRQMQRY REPORT OUTPUT(*OUTFILE) OUTFILE(QGPL/OUT)"));
        Assert.Contains(_system.Jobs.GetLog(_session.Job.Key, 1000), entry => entry.MessageId == "SQL0445" && entry.Text!.Contains("SQLSTATE=01004"));
    }
}
