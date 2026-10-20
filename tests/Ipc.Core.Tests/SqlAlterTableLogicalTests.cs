using Ipc.Core.Messages;
using Ipc.Db.Dds;

namespace Ipc.Core.Tests;

public sealed partial class SqlAlterTableTests
{
    private void CreateAlterLogical(string name, string format, params string[] clauses)
    {
        var source = string.Join('\n', new[] { LogicalFileTests.Line('R', format, "PFILE(QGPL/DATA)") }.Concat(clauses));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile(name, source);
        _files.CreateLogicalFile("QGPL", name, definition, source);
    }
    [Fact]
    public void Shared_DDS_formats_follow_added_and_dropped_nonkey_columns_and_keep_key_navigation()
    {
        CreateAlterLogical("SHARED", "DATA", LogicalFileTests.Line('K', "ID"));
        Run("ALTER TABLE QGPL/DATA ADD FLAG INTEGER NOT NULL DEFAULT 5 DROP AMOUNT RESTRICT");
        Assert.Equal(new[] { "ID", "CODE", "FLAG" }, _files.GetDefinition("QGPL", "SHARED")!.PrimaryFormat.Fields.Select(field => field.Name));
        var row = Assert.Single(_files.ReadKeyPrefix("QGPL", "SHARED", "SHARED", new Dictionary<string, object?> { ["ID"] = 2 })); Assert.Equal(5L, row["FLAG"]);
    }
    [Fact]
    public void A_DDS_projection_rebinds_changed_types_and_revalidates_select_omit_constants()
    {
        CreateAlterLogical("PROJECT", "PROJECTREC", LogicalFileTests.Line(' ', "ID"), LogicalFileTests.Line(' ', "CODE"), LogicalFileTests.Line('K', "ID"), LogicalFileTests.Line('S', "CODE", "COMP(EQ 'abcdef')"));
        var before = _files.GetDefinition("QGPL", "DATA")!.ToJson();
        Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA ALTER CODE SET DATA TYPE VARCHAR(3)"));
        Assert.Equal(before, _files.GetDefinition("QGPL", "DATA")!.ToJson()); Assert.Single(_files.ReadAll("QGPL", "PROJECT", "PROJECT"));
        Run("ALTER TABLE QGPL/DATA ALTER ID SET DATA TYPE BIGINT");
        Assert.Equal(8, _files.GetDefinition("QGPL", "PROJECT")!.PrimaryFormat.Find("ID")!.Length);
        Assert.Equal(1L, Assert.Single(_files.ReadAll("QGPL", "PROJECT", "PROJECT"))["ID"]);
    }
    [Fact]
    public void Dropping_all_columns_of_a_key_removes_it_under_RESTRICT_but_partial_keys_reject()
    {
        Run("ALTER TABLE QGPL/DATA ADD CONSTRAINT PAIR UNIQUE(ID,CODE)");
        Assert.Equal("SQL0478", Assert.Throws<CpfException>(() => Run("ALTER TABLE QGPL/DATA DROP CODE RESTRICT")).MessageId);
        Run("ALTER TABLE QGPL/DATA DROP ID RESTRICT DROP CODE RESTRICT");
        Assert.Empty(_files.GetDefinition("QGPL", "DATA")!.SqlTable!.Constraints); Assert.Single(_files.GetDefinition("QGPL", "DATA")!.PrimaryFormat.Fields);
    }
}
