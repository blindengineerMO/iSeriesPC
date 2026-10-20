using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests
{
    [Fact]
    public void Journal_rewrite_emits_no_false_record_entries_and_later_rows_use_the_new_schema()
    {
        _system.Journals.CreateReceiver(new("QGPL", "ALTRCV")); _system.Journals.Create(new("QGPL", "ALTJRN"), new("QGPL", "ALTRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "ALTJRN"), "*BOTH");
        Run("UPDATE QGPL/DATA SET AMOUNT=10 WHERE ID=1"); var before = _system.Journals.Read(new("QGPL", "ALTJRN"));
        Run("ALTER TABLE QGPL/DATA ADD N INTEGER NOT NULL DEFAULT 5 ALTER CODE SET DATA TYPE VARCHAR(5)");
        Assert.Equal(before.Select(entry => entry.Sequence), _system.Journals.Read(new("QGPL", "ALTJRN")).Select(entry => entry.Sequence));
        Run("UPDATE QGPL/DATA SET N=6 WHERE ID=1");
        var after = _system.Journals.Read(new("QGPL", "ALTJRN")); Assert.Equal(new[] { "UB", "UP", "UB", "UP" }, after.Select(entry => entry.Type));
        Assert.Contains(after[^1].After!, cell => cell.Name == "N" && cell.Value == "6");
        Assert.Equal(3L, Scalar("SELECT count(*) FROM sys_journal_triggers")); Assert.Equal(3L, Scalar("SELECT count(*) FROM sys_record_slot_triggers"));
    }
    [Fact]
    public void Application_hooks_are_recreated_without_firing_during_existing_row_conversion()
    {
        Scalar("CREATE TABLE seen(n INTEGER); CREATE TRIGGER app_hook AFTER UPDATE ON \"QGPL.DATA.DATA\" BEGIN INSERT INTO seen VALUES(NEW.ID); END");
        Run("ALTER TABLE QGPL/DATA ADD N INTEGER ALTER CODE SET DATA TYPE VARCHAR(5)");
        Assert.Equal(0L, Scalar("SELECT count(*) FROM seen")); Run("UPDATE QGPL/DATA SET N=1 WHERE ID=2"); Assert.Equal(2L, Scalar("SELECT n FROM seen"));
    }
    [Fact]
    public void Adding_implicit_datetime_defaults_uses_native_historical_values_for_existing_rows()
    {
        Run("ALTER TABLE QGPL/DATA ADD D DATE NOT NULL ADD T TIME NOT NULL ADD TS TIMESTAMP NOT NULL ADD EX DATE NOT NULL DEFAULT CURRENT_DATE");
        var row = Query("SELECT D,T,TS,EX FROM QGPL/DATA WHERE ID=1")[0];
        Assert.Equal("0001-01-01", row[0]); Assert.Equal("00:00:00", row[1]); Assert.Equal("0001-01-01 00:00:00.000000", row[2]); Assert.NotEqual("0001-01-01", row[3]);
        Run("INSERT INTO QGPL/DATA(ID) VALUES(3)"); Assert.NotEqual("0001-01-01", Query("SELECT D FROM QGPL/DATA WHERE ID=3")[0][0]);
    }
    [Fact]
    public void UTF8_shrinking_truncates_at_character_boundaries_and_retains_the_column_CCSID()
    {
        var options = Options with { Ccsid = 1208 };
        _files.ExecuteSql("CREATE TABLE QGPL/TEXTDATA(VALUE VARCHAR(12))", options); _files.ExecuteSql("INSERT INTO QGPL/TEXTDATA VALUES('東京A')", options);
        Run("ALTER TABLE QGPL/TEXTDATA ALTER VALUE SET DATA TYPE VARCHAR(5)");
        Assert.Equal("東", Query("SELECT VALUE FROM QGPL/TEXTDATA")[0][0]); Assert.Equal(1208, _files.GetDefinition("QGPL", "TEXTDATA")!.PrimaryFormat.Find("VALUE")!.Ccsid);
    }
    [Theory]
    [InlineData("ALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE INTEGER", "SQL0190")]
    [InlineData("ALTER TABLE QGPL/DATA DROP ID DROP CODE DROP AMOUNT", "SQL0104")]
    [InlineData("ALTER TABLE QGPL/DATA ADD ID INTEGER", "SQL0612")]
    [InlineData("ALTER TABLE QGPL/DATA ADD FLAG INTEGER ALTER FLAG SET DEFAULT 5", "SQL0612")]
    [InlineData("ALTER TABLE QGPL/DATA ALTER CODE SET DEFAULT NULL ALTER CODE SET NOT NULL", "SQL0407")]
    [InlineData("ALTER TABLE QGPL/DATA ALTER ID DROP DEFAULT", "SQL0190")]
    [InlineData("ALTER TABLE QGPL/DATA ADD FLAG INTEGER DROP FLAG", "SQL0612")]
    [InlineData("ALTER TABLE QGPL/DATA DROP CONSTRAINT MISSING", "SQL0204")]
    [InlineData("ALTER TABLE QGPL/DATA DROP CODE ADD UNIQUE(CODE)", "SQL0206")]
    public void Invalid_alterations_fail_without_changing_schema_or_rows(string sql, string error)
    {
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson(); Assert.Equal(error, Assert.Throws<CpfException>(() => Run(sql)).MessageId);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson()); Assert.Equal(2, Query("SELECT ID FROM QGPL/DATA").Count);
    }
    [Fact]
    public void Only_dropped_columns_referenced_by_a_view_are_subject_to_its_RESTRICT_dependency()
    {
        Run("CREATE VIEW QGPL/REPORT AS SELECT CODE FROM QGPL/DATA");
        Run("ALTER TABLE QGPL/DATA DROP CODE CASCADE DROP AMOUNT RESTRICT");
        Assert.False(_files.FileExists("QGPL", "REPORT")); Assert.Single(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Fields);
    }
    [Fact]
    public void Altered_index_formats_and_exact_unique_keys_follow_new_numeric_attributes()
    {
        Run("CREATE UNIQUE INDEX QGPL/BYVALUE ON QGPL/DATA(AMOUNT) RCDFMT IXREC ADD KEYS ONLY");
        Run("ALTER TABLE QGPL/DATA ALTER AMOUNT SET DATA TYPE DECIMAL(29,1)");
        Assert.Equal(1, _files.GetDefinition("QGPL", "BYVALUE")!.PrimaryFormat.Find("AMOUNT")!.Decimals);
        Assert.Equal("SQL0803", Assert.Throws<CpfException>(() => Run("INSERT INTO QGPL/DATA VALUES(3,'x',10.2)")).MessageId);
        Assert.Equal(2, Query("SELECT AMOUNT FROM QGPL/BYVALUE").Count);
    }
    [Fact]
    public void Metadata_hooks_cannot_replace_the_intended_definition_or_transfer_ownership_during_ALTER()
    {
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Scalar("CREATE TRIGGER replace_def AFTER UPDATE OF def ON sys_file_defs WHEN NEW.name='DATA' AND json_array_length(json_extract(NEW.def,'$.formats[0].fields'))=4 BEGIN UPDATE sys_file_defs SET def=OLD.def WHERE name='DATA'; END");
        Assert.Equal("SQL0104", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ADD N INTEGER")).MessageId);
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson());
    }
}
