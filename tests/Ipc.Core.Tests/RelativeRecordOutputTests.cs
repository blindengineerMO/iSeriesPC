using Ipc.Core.Messages;
using Ipc.Db.Dds;
using Ipc.Db.Store;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Relative_output_preserves_extent_and_uses_physical_defaults_through_a_logical_file(bool logical)
    {
        var file=logical?DescendingView():"NAV";
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=8");
        using var writer=_files.OpenRecordWriter("QGPL",file,file);
        writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=99L});
        using var cursor=_files.OpenSequentialCursor("QGPL","NAV","NAV",keyed:false);
        Assert.True(cursor.ChainRecord(8));Assert.Equal(99,Id(cursor.Current));Assert.Equal("",cursor.Current!["TEXT"]);Assert.Null(cursor.Current["AMOUNT"]);
        Assert.Equal(8,Extent()); Assert.Equal(8,_files.RowCount("QGPL","NAV","NAV"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Relative_output_rolls_back_late_trigger_failure_and_can_retry_the_same_slot(bool logical)
    {
        var file=logical?DescendingView():"NAV";
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=8; CREATE TRIGGER reject_relative AFTER INSERT ON \"QGPL.NAV.NAV\" BEGIN SELECT RAISE(ABORT,'reject'); END");
        using var writer=_files.OpenRecordWriter("QGPL",file,file);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=99L}));
        Assert.Equal(8,Extent());Assert.Equal(7,_files.RowCount("QGPL","NAV","NAV"));
        Sql("DROP TRIGGER reject_relative");writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=99L});
        Assert.Equal(8,_files.RowCount("QGPL","NAV","NAV"));
    }
    [Fact]
    public void Relative_output_respects_logical_selection_before_filling_a_slot()
    {
        var source=string.Join('\n',LogicalFileTests.Line('R',"VREC","PFILE(QGPL/NAV)"),LogicalFileTests.Line('S',"ID","COMP(GT 10)"));
        var definition=new LogicalDdsCompiler(_=>(new("QGPL","NAV"),_files.GetDefinition("QGPL","NAV")!)).Compile("SELECTED",source);
        _files.CreateLogicalFile("QGPL","SELECTED",definition,source);
        Sql("DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=8");
        using var writer=_files.OpenRecordWriter("QGPL","SELECTED","SELECTED");
        Assert.Throws<CpfException>(()=>writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=9L}));
        Assert.Equal(7,_files.RowCount("QGPL","NAV","NAV"));
        writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=99L});Assert.Equal(8,Extent());
    }
    [Fact]
    public void Relative_output_obeys_unique_constraints_without_consuming_the_deleted_slot()
    {
        Sql("CREATE UNIQUE INDEX unique_relative_id ON \"QGPL.NAV.NAV\"(ID); DELETE FROM \"QGPL.NAV.NAV\" WHERE rowid=8");
        using var writer=_files.OpenRecordWriter("QGPL","NAV","NAV");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=1L}));
        writer.WriteRelative(8,new Dictionary<string,object?>{["ID"]=99L});Assert.Equal(8,Extent());
    }
}

public sealed partial class RpgDiskFileTests
{
    [Fact]
    public void Relative_output_validates_owner_cancellation_rebind_and_disposal_before_writing()
    {
        _files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:true);
        using var session=Session();using var other=Session();using var owner=OperationIdentity.Enter("QSECOFR",session.Job.Key);
        using var cancellation=new CancellationTokenSource();var writer=_files.OpenRecordWriter("QGPL","DATA","DATA",cancellation.Token);
        try
        {
            cancellation.Cancel();Assert.ThrowsAny<OperationCanceledException>(()=>writer.WriteRelative(5,AddedValues()));
            writer.RebindCancellation(CancellationToken.None);
            using(OperationIdentity.Enter("QSECOFR",other.Job.Key))Assert.Equal("CPF9802",Assert.Throws<CpfException>(()=>writer.WriteRelative(5,AddedValues())).MessageId);
            writer.WriteRelative(5,AddedValues());Assert.Equal("added",Text(104));
        }
        finally{writer.Dispose();}
        Assert.Throws<ObjectDisposedException>(()=>writer.WriteRelative(4,AddedValues()));Assert.Empty(Locks());
    }
}

public sealed partial class RecordTransactionTests
{
    [Fact]
    public void Relative_output_journals_the_requested_record_number_and_rejects_late_enlistment()
    {
        _files.InitializeMember("QGPL","FIRST",totalRecords:5,deleted:true);
        var before=_system.Journals.Read(Journal).Max(e=>e.Sequence);
        using var owner=OperationIdentity.Enter("QSECOFR",_job.Key);
        using var writer=_files.OpenRecordWriter("QGPL","FIRST","FIRST");
        writer.WriteRelative(5,new Dictionary<string,object?>{["ID"]=5L,["TEXT"]="relative"});
        var entry=Assert.Single(_system.Journals.Read(Journal,before+1));Assert.Equal("PT",entry.Type);Assert.Equal(5L,entry.RecordNumber);Assert.Equal(_job.Key.ToString(),entry.Job);Assert.Null(entry.CommitCycle);
        using(var cursor=_files.OpenSequentialCursor("QGPL","FIRST","FIRST",keyed:false)){Assert.True(cursor.ChainRecord(5));Assert.Equal(5L,cursor.Current!["ID"]);}
        Begin();Assert.Throws<CpfException>(()=>writer.WriteRelative(4,new Dictionary<string,object?>{["ID"]=4L}));
        Assert.Equal(0,_system.RecordTransactions.Inspect(_job.Key).PendingChanges);
        _system.RecordTransactions.Rollback(_job.Key);_system.RecordTransactions.End(_job.Key);Assert.Equal(2,_files.RowCount("QGPL","FIRST","FIRST"));
    }
}
