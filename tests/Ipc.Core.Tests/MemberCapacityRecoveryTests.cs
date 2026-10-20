using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class MemberCapacityRecoveryTests:IDisposable
{
    private readonly IpcSystem _system=IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    private readonly MessageQueueStore _messages;
    private readonly QualifiedName _operator=new("QSYS","QSYSOPR");
    public MemberCapacityRecoveryTests()
    {
        _system.Start();_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"CapacityFixtureAdmin41");_files=new(_system.Connections,_system.Objects);_messages=new(_system.Connections);
        var format=new RecordFormat{Name="ROW",Fields=[new(){Name="ID",Type=FieldType.Binary,Length=8}]};format.AssignPositions();
        foreach(var file in new[]{"DATA","OTHER"})_files.CreatePhysicalFile("QGPL",file,new(){Name=file,Attribute=FileAttribute.Physical,MemberSize=new(1,2,1),Formats=[format]},"");
        for(var id=1;id<=3;id++)Add(id);
    }
    private void Add(long id,CancellationToken token=default)=>_files.Insert("QGPL","DATA","DATA","ROW",new Dictionary<string,object?>{["ID"]=id},token);
    private object? Scalar(string sql){using var connection=_system.Connections.Open();using var command=connection.CreateCommand();command.CommandText=sql;return command.ExecuteScalar();}
    private static ProgramBuffer Reply(string value)=>new(CodePage.ToBytes(37,value),37);
    private Job Job()=>_system.Jobs.CreateInteractive("QSECOFR");
    private async Task<QueuedMessage> Inquiry()
    {
        var limit=DateTime.UtcNow.AddSeconds(10);
        while(true){var message=_messages.List(_operator).FirstOrDefault(m=>m.MessageId=="CPA5305"&&!m.Replied);if(message is not null)return message;Assert.True(DateTime.UtcNow<limit,"Expected member-full inquiry.");await Task.Delay(20);}
    }
    [Theory]
    [InlineData("I",5L)] [InlineData("+1",5L)] [InlineData("+0001",5L)] [InlineData("3",9L)] [InlineData("9999",20001L)]
    public void Reply_list_grants_the_requested_increments_and_retries_once(string reply,long capacity)
    {
        new SystemReplyList(_system.Connections).Put(new(1,"CPA5305",Reply:reply));var job=Job();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);new InquiryReplyPolicy(_system.Connections).Set(job.Key,"*SYSRPYL");
        Add(4);Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(capacity,Scalar("SELECT capacity FROM sys_member_capacity"));
        var message=Assert.Single(_messages.List(_operator));Assert.Equal("CPA5305",message.MessageId);Assert.Equal("QCPFMSG",message.Predefined!.File);Assert.True(message.Replied);
        Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_size_waits"));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_program_message_queues WHERE program='QDBSIZE'"));
    }
    [Fact]
    public void Default_reply_cancels_without_capacity_or_record_changes()
    {
        var job=Job();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);new InquiryReplyPolicy(_system.Connections).Set(job.Key,"*DFT");
        Assert.Equal("CPF5030",Assert.Throws<CpfException>(()=>Add(4)).MessageId);Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));
    }
    [Theory]
    [InlineData("I",false)] [InlineData("C",true)]
    public async Task Manual_operator_reply_uses_no_held_writer_and_restores_job_state(string reply,bool cancel)
    {
        var job=Job();using var cancellation=new CancellationTokenSource();
        var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            var inquiry=await Inquiry();_files.Insert("QGPL","OTHER","OTHER","ROW",new Dictionary<string,object?>{["ID"]=1L});
            Assert.False(work.IsCompleted);_messages.Reply(_operator,inquiry.Key,Reply(reply));var error=await work.WaitAsync(TimeSpan.FromSeconds(10));
            if(cancel)Assert.Equal("CPF5030",Assert.IsType<CpfException>(error).MessageId);else Assert.Null(error);
            Assert.Equal(JobStatus.Active,_system.Jobs.GetRequired(job.Key).Status);Assert.Equal(cancel?3:4,_files.RowCount("QGPL","DATA","DATA"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public async Task Canceling_the_wait_removes_private_queues_and_leaves_no_live_operator_request()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            await Inquiry();cancellation.Cancel();Assert.IsAssignableFrom<OperationCanceledException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(JobStatus.Active,_system.Jobs.GetRequired(job.Key).Status);Assert.All(_messages.List(_operator),m=>Assert.True(m.Replied));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_size_waits"));Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public void Trusted_jobless_calls_report_full_without_waiting()
    {Assert.Equal("CPF5030",Assert.Throws<CpfException>(()=>Add(4)).MessageId);Assert.Empty(_messages.List(_operator));}
    private void Sql(string sql){using var connection=_system.Connections.Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    private Job Automatic(string reply="I")
    {
        new SystemReplyList(_system.Connections).Put(new(1,"CPA5305",Reply:reply));var job=Job();new InquiryReplyPolicy(_system.Connections).Set(job.Key,"*SYSRPYL");return job;
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Open_physical_and_logical_writers_share_source_member_capacity(bool logical)
    {
        if(logical)
        {
            var source=LogicalFileTests.Line('R',"VROW","PFILE(QGPL/DATA)");var definition=new Ipc.Db.Dds.LogicalDdsCompiler(_=>(new("QGPL","DATA"),_files.GetDefinition("QGPL","DATA")!)).Compile("VIEW",source);
            _files.CreateLogicalFile("QGPL","VIEW",definition,source);
        }
        var job=Automatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        using var writer=_files.OpenRecordWriter("QGPL",logical?"VIEW":"DATA",logical?"VIEW":"DATA");writer.Write(new Dictionary<string,object?>{["ID"]=4L});
        Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(5L,Scalar("SELECT capacity FROM sys_member_capacity"));Assert.Single(_messages.List(_operator));
    }
    [Fact]
    public void Clear_resets_operator_capacity_and_changes_to_other_attributes_preserve_it()
    {
        var job=Automatic();using(OperationIdentity.Enter("QSECOFR",job.Key))Add(4);
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,reuseDeletedRecords:true);Assert.Equal(5L,Scalar("SELECT capacity FROM sys_member_capacity"));
        _files.ClearMember("QGPL","DATA","DATA");Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));
        Add(1);Add(2);Add(3);Assert.Throws<CpfException>(()=>Add(4));
    }
    [Fact]
    public void Explicit_SIZE_changes_reset_grants_while_SAME_preserves_them()
    {
        var job=Automatic();using(OperationIdentity.Enter("QSECOFR",job.Key))Add(4);
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,memberSize:new());Assert.Equal(5L,Scalar("SELECT capacity FROM sys_member_capacity"));
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,memberSize:new(1,3,1));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));Assert.Throws<CpfException>(()=>Add(5));
    }
    [Fact]
    public void Initialization_can_consume_previously_granted_capacity_without_a_second_inquiry()
    {
        var job=Automatic();using(OperationIdentity.Enter("QSECOFR",job.Key))Add(4);
        Assert.Equal(1,_files.InitializeMember("QGPL","DATA",deleted:true));Assert.Equal(5L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        Assert.Single(_messages.List(_operator));Assert.Throws<CpfException>(()=>_files.InitializeMember("QGPL","DATA",totalRecords:6,deleted:true));
    }
    [Fact]
    public void Object_copy_and_move_preserve_capacity_and_member_removal_cascades_it()
    {
        var job=Automatic();using(OperationIdentity.Enter("QSECOFR",job.Key))Add(4);
        var objects=new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections);objects.Relocate(new("QGPL","DATA"),ObjectType.File,new("QGPL","COPIED"),copy:true);
        _files.Insert("QGPL","COPIED","DATA","ROW",new Dictionary<string,object?>{["ID"]=5L});Assert.Equal(2L,Scalar("SELECT count(*) FROM sys_member_capacity"));
        objects.Relocate(new("QGPL","COPIED"),ObjectType.File,new("QGPL","MOVED"));Assert.Throws<CpfException>(()=>_files.Insert("QGPL","MOVED","DATA","ROW",new Dictionary<string,object?>{["ID"]=6L}));
        _files.RemoveMember("QGPL","MOVED","DATA");Assert.Equal(1L,Scalar("SELECT count(*) FROM sys_member_capacity"));
    }
    [Fact]
    public void Accepted_capacity_survives_a_later_trigger_failure_without_publishing_the_failed_record()
    {
        Sql("CREATE TRIGGER reject_capacity AFTER INSERT ON \"QGPL.DATA.DATA\" BEGIN SELECT RAISE(ABORT,'reject'); END");
        var job=Automatic();using(OperationIdentity.Enter("QSECOFR",job.Key))Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>Add(4));
        Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(5L,Scalar("SELECT capacity FROM sys_member_capacity"));
        Sql("DROP TRIGGER reject_capacity");Add(4);Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));
    }
    [Fact]
    public async Task Copy_retries_keep_the_original_source_snapshot_across_the_operator_wait()
    {
        _files.Insert("QGPL","OTHER","OTHER","ROW",new Dictionary<string,object?>{["ID"]=11L});var job=Job();using var cancellation=new CancellationTokenSource();
        var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return _files.CopyRecords(new("QGPL","OTHER"),"OTHER",new("QGPL","DATA"),"DATA",cancellationToken:cancellation.Token);});
        try
        {
            var inquiry=await Inquiry();Sql("UPDATE \"QGPL.OTHER.OTHER\" SET ID=55");_files.Insert("QGPL","OTHER","OTHER","ROW",new Dictionary<string,object?>{["ID"]=66L});
            _messages.Reply(_operator,inquiry.Key,Reply("I"));Assert.Equal(1,await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(new long[]{1,2,3,11},_files.ReadAll("QGPL","DATA","DATA").Select(row=>(long)row["ID"]!).Order().ToArray());
        }
        finally{cancellation.Cancel();try{await work.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}}
    }
    [Fact]
    public void Replace_copy_can_retry_atomically_after_an_operator_grant()
    {
        _files.ChangePhysicalMemberLimit("QGPL","OTHER",null,memberSize:new(Unlimited:true));for(var id=21;id<=26;id++)_files.Insert("QGPL","OTHER","OTHER","ROW",new Dictionary<string,object?>{["ID"]=(long)id});
        var job=Automatic("2");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);Assert.Equal(6,_files.CopyRecords(new("QGPL","OTHER"),"OTHER",new("QGPL","DATA"),"DATA",replace:true));
        Assert.Equal(new long[]{21,22,23,24,25,26},_files.ReadAll("QGPL","DATA","DATA").Select(row=>(long)row["ID"]!).Order().ToArray());Assert.Equal(7L,Scalar("SELECT capacity FROM sys_member_capacity"));
    }
    [Fact]
    public async Task A_recreated_member_cannot_receive_the_old_inquirys_capacity_grant()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            var inquiry=await Inquiry();_files.RemoveMember("QGPL","DATA","DATA");_files.AddMember("QGPL","DATA","DATA");
            _messages.Reply(_operator,inquiry.Key,Reply("I"));Assert.IsType<CpfException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(0,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public async Task Ending_the_job_closes_pending_capacity_inquiries_without_resurrecting_it()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            await Inquiry();_system.Jobs.RequestEnd(job.Key);Assert.NotNull(await work.WaitAsync(TimeSpan.FromSeconds(10)));
            _system.Jobs.Complete(job.Key,JobCompletion.Abnormal);Assert.Equal(JobExecutionState.Cancelled,_system.Jobs.GetRequired(job.Key).ExecutionState);Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_size_waits"));Assert.All(_messages.List(_operator),message=>Assert.True(message.Replied));Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public void A_zero_increment_cannot_authorize_a_capacity_increase()
    {
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,memberSize:new(3,0,0));var job=Automatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        Assert.Equal("CPF5030",Assert.Throws<CpfException>(()=>Add(4)).MessageId);Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));Assert.Single(_messages.List(_operator));
    }
    [Fact]
    public void The_retried_write_generates_one_job_attributed_journal_entry()
    {
        _system.Journals.CreateReceiver(new("QGPL","CAPRCV"));_system.Journals.Create(new("QGPL","CAPJRN"),new("QGPL","CAPRCV"));_system.Journals.StartFile(new("QGPL","DATA"),new("QGPL","CAPJRN"),"*BOTH");
        var job=Automatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);Add(4);
        var entry=Assert.Single(_system.Journals.Read(new("QGPL","CAPJRN")));Assert.Equal("PT",entry.Type);Assert.Equal(4,entry.RecordNumber);Assert.Equal(job.Key.ToString(),entry.Job);
    }
    [Fact]
    public async Task Revoked_add_authority_prevents_a_grant_and_record_retry()
    {
        _system.Security.Profiles.Create(new(){Name="ADDUSER"});_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("ADDUSER"),"CapacityUserFixture42");
        Sql("UPDATE sys_objects SET public_authority=1 WHERE lib='QGPL' AND name='DATA' AND type='*FILE'");
        _system.Security.Authority.Grant("QGPL","DATA",ObjectType.File,"ADDUSER",AuthorityBit.ObjectOperate|AuthorityBit.Add);
        var job=_system.Jobs.CreateInteractive("ADDUSER");using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("ADDUSER",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            var inquiry=await Inquiry();_system.Security.Authority.Grant("QGPL","DATA",ObjectType.File,"ADDUSER",AuthorityBit.ObjectOperate);
            _messages.Reply(_operator,inquiry.Key,Reply("I"));Assert.Equal("CPF9802",Assert.IsType<CpfException>(await work.WaitAsync(TimeSpan.FromSeconds(10))).MessageId);
            Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public async Task Invalid_manual_replies_keep_the_inquiry_available_for_a_valid_reply()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(4,cancellation.Token));});
        try
        {
            var inquiry=await Inquiry();foreach(var invalid in new[]{"bad","-1","0","10000"})Assert.Equal("CPF2422",Assert.Throws<CpfException>(()=>_messages.Reply(_operator,inquiry.Key,Reply(invalid))).MessageId);
            Assert.False(work.IsCompleted);_messages.Reply(_operator,inquiry.Key,Reply("2"));Assert.Null(await work.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(7L,Scalar("SELECT capacity FROM sys_member_capacity"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public void Interrupted_job_cleanup_closes_a_persisted_member_inquiry_even_without_its_worker()
    {
        var job=Job();using(OperationIdentity.Enter("QSECOFR",job.Key))
        {
            var queue=_messages.OpenMemberSizeReplyQueue(job.Key,CancellationToken.None);
            var data=new byte[42];for(var index=0;index<4;index++)CodePage.ToBytes(37,new[]{"DATA","DATA","QGPL","ROW"}[index].PadRight(10)).CopyTo(data,index*10);
            var snapshot=new MessageDescriptionStore(_system.Connections).Snapshot("QSYS","QCPFMSG","CPA5305","QSYS",new(data,37));var formatted=MessageDescriptionFormat.Format(snapshot.Description,snapshot.Replacement,37);
            _messages.SendTo([new(_operator)],formatted.Text,"INQ",queue,messageId:"CPA5305",severity:99,senderCopy:true,predefined:snapshot);
        }
        _system.Jobs.Complete(job.Key,JobCompletion.Abnormal,outcome:JobExecutionState.Interrupted);
        Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_size_waits"));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_program_message_queues WHERE program='QDBSIZE'"));Assert.All(_messages.List(_operator),message=>Assert.True(message.Replied));
    }
    [Fact]
    public void Failed_grant_rolls_back_capacity_metadata_without_publishing_a_record()
    {
        Sql("CREATE TRIGGER reject_capacity_grant AFTER INSERT ON sys_member_capacity BEGIN SELECT RAISE(ABORT,'reject grant'); END");
        var job=Automatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(()=>Add(4));
        Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));
        Sql("DROP TRIGGER reject_capacity_grant");Add(4);Assert.Equal(4,_files.RowCount("QGPL","DATA","DATA"));
    }
    [Fact]
    public async Task Recreated_copy_sources_cannot_authorize_an_obsolete_copy_capacity_grant()
    {
        _files.Insert("QGPL","OTHER","OTHER","ROW",new Dictionary<string,object?>{["ID"]=11L});var job=Job();using var cancellation=new CancellationTokenSource();
        var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>_files.CopyRecords(new("QGPL","OTHER"),"OTHER",new("QGPL","DATA"),"DATA",cancellationToken:cancellation.Token));});
        try
        {
            var inquiry=await Inquiry();_files.RemoveMember("QGPL","OTHER","OTHER");_files.AddMember("QGPL","OTHER","OTHER");_messages.Reply(_operator,inquiry.Key,Reply("I"));
            Assert.IsType<CpfException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));
        }
        finally{cancellation.Cancel();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    public void Dispose()=>_system.Dispose();
}
