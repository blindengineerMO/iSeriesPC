using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class InquiryReplyTests:IDisposable
{
    private readonly IpcSystem _system=IpcSystem.Create(":memory:");
    private readonly MessageQueueStore _queues;
    private readonly MessageDescriptionStore _descriptions;
    private readonly SystemReplyList _entries;
    private readonly InquiryReplyPolicy _policy;
    private readonly QualifiedName _inbox=new("QGPL","INBOX"),_replies=new("QGPL","REPLIES");
    public InquiryReplyTests()
    {
        _system.Start();_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"InquiryReplyFixture41");
        _queues=new(_system.Connections);_queues.Create(_inbox);_queues.Create(_replies);
        _descriptions=new(_system.Connections);_descriptions.Create("QGPL","TEXTS");
        _descriptions.Add("QGPL","TEXTS","USR1234",new MessageDescription("Continue &1?","",0,[new("*CHAR",4)],37,"N",ReplyRules:new("*CHAR",1,Values:["Y","N"])));
        _entries=new(_system.Connections);_policy=new(_system.Connections);
    }
    private static ProgramBuffer Bytes(string text,int ccsid=37)=>new(CodePage.ToBytes(ccsid,text),ccsid);
    private uint Send(string data="ABCD",int ccsid=37,Action<IReadOnlyList<uint>>? prepare=null)
    {
        var snapshot=_descriptions.Snapshot("QGPL","TEXTS","USR1234","QGPL",Bytes(data,ccsid));
        var formatted=MessageDescriptionFormat.Format(snapshot.Description,snapshot.Replacement,ccsid);
        return Assert.Single(_queues.SendTo([new(_inbox)],formatted.Text,"INQ",new(_replies),messageId:"USR1234",predefined:snapshot,senderCopy:true,prepare:prepare));
    }
    [Theory]
    [InlineData("*RQD",false,null)] [InlineData("*DFT",true,"23")] [InlineData("*SYSRPYL",false,null)]
    public void Job_policy_controls_automatic_default_and_manual_fallback(string mode,bool automatic,string? type)
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,mode);
        var copy=Send();Assert.Equal(automatic,Assert.Single(_queues.List(_inbox)).Replied);
        var reply=_queues.Receive(_replies,MessageSelection.Reply,copy);
        if(!automatic)Assert.Null(reply);else{Assert.Equal("N",reply!.Data.ToText());Assert.Equal(type,reply.ReturnType);}
    }
    [Theory]
    [InlineData("USR1234")] [InlineData("USR1200")] [InlineData("USR0000")] [InlineData("*ANY")]
    public void Reply_list_matches_exact_and_native_generic_identifiers(string identifier)
    {
        _entries.Put(new(10,identifier,Reply:"Y"));var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");
        var copy=Send();var reply=_queues.Receive(_replies,MessageSelection.Reply,copy)!;Assert.Equal("Y",reply.Data.ToText());Assert.Equal("25",reply.ReturnType);
    }
    [Theory]
    [InlineData("*RQD",false,null)] [InlineData("bad",false,null)] [InlineData("*DFT",true,"N")] [InlineData("Y",true,"Y")]
    public void First_matching_entry_decides_reply_and_invalid_value_requires_manual_reply(string value,bool automatic,string? expected)
    {
        _entries.Put(new(20,"USR1234",Reply:"Y"));_entries.Put(new(10,"USR0000",Reply:value));
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");var copy=Send();
        Assert.Equal(automatic,Assert.Single(_queues.List(_inbox)).Replied);var reply=_queues.Receive(_replies,MessageSelection.Reply,copy);
        if(automatic){Assert.Equal(expected,reply!.Data.ToText());Assert.Equal("25",reply.ReturnType);}else Assert.Null(reply);
    }
    [Theory]
    [InlineData(37,37)] [InlineData(37,1208)] [InlineData(1208,37)]
    public void Comparison_uses_replacement_data_at_the_specified_position_with_CCSID_conversion(int stored,int message)
    {
        _entries.Put(new(10,"USR1234",CodePage.ToBytes(stored,"BC"),2,stored,"Y"));
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");var copy=Send(ccsid:message);
        Assert.Equal("25",_queues.Receive(_replies,MessageSelection.Reply,copy)!.ReturnType);
    }
    [Theory]
    [InlineData("XYZ",1)] [InlineData("ABCD",2)] [InlineData("A",999)]
    public void Missing_or_short_comparison_leaves_the_inquiry_unanswered(string comparison,int offset)
    {
        _entries.Put(new(10,"USR1234",CodePage.ToBytes(37,comparison),offset,Reply:"Y"));
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");var copy=Send();
        Assert.Null(_queues.Receive(_replies,MessageSelection.Reply,copy));Assert.False(Assert.Single(_queues.List(_inbox)).Replied);
    }
    [Fact]
    public void Immediate_messages_are_not_matched_by_the_system_reply_list()
    {
        _entries.Put(new(10,Reply:"Y"));var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");
        var copy=Assert.Single(_queues.SendTo([new(_inbox)],Bytes("immediate"),"INQ",new(_replies),senderCopy:true));Assert.Null(_queues.Receive(_replies,MessageSelection.Reply,copy));
    }
    [Fact]
    public void Automatic_reply_and_sender_copy_roll_back_when_the_send_receiver_cannot_be_prepared()
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*DFT");
        Assert.Throws<InvalidOperationException>(()=>Send(prepare:_=>throw new InvalidOperationException("receiver")));
        Assert.Empty(_queues.List(_inbox));Assert.Empty(_queues.List(_replies));
    }
    [Fact]
    public void Policy_change_takes_effect_for_later_sends_without_answering_old_inquiries()
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);var old=Send();_policy.Set(job.Key,"*DFT");var current=Send();
        Assert.Null(_queues.Receive(_replies,MessageSelection.Reply,old));Assert.Equal("23",_queues.Receive(_replies,MessageSelection.Reply,current)!.ReturnType);
    }
    [Fact]
    public void Reply_list_changes_require_an_authorized_operator_and_job_policy_is_job_owned()
    {
        _system.Security.Profiles.Create(new(){Name="ORDINARY"});var target=_system.Jobs.CreateInteractive("QSECOFR");var ordinary=_system.Jobs.CreateInteractive("ORDINARY");
        using var owner=OperationIdentity.Enter("ORDINARY",ordinary.Key);
        Assert.Throws<CpfException>(()=>_entries.Put(new(1)));Assert.Throws<CpfException>(()=>_policy.Set(target.Key,"*DFT"));_policy.Set(ordinary.Key,"*DFT");Assert.Equal("*DFT",_policy.Get(ordinary.Key));
    }
    [Fact]
    public void Commands_manage_entries_and_capture_job_description_policy_at_submission()
    {
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None);
        foreach(var command in new[]{"ADDRPYLE 10 MSGID(USR0000) RPY(Y)","CHGRPYLE 10 RPY(*DFT)","CHGJOB INQMSGRPY(*SYSRPYL)","CRTJOBD JOBD(QGPL/INQUIRY) INQMSGRPY(*DFT)"})
        {var result=session.Execute(command);Assert.False(result.IsError,result.Message);}
        Assert.Equal("*DFT",Assert.Single(_entries.List()).Reply);Assert.Equal("*SYSRPYL",_policy.Get(session.Job.Key));
        var submitted=_system.Jobs.Submit("INQUIRY",description:"inquiry fixture",jobq:"QUSRSYS/QBATCH",priority:9,jobDescription:"QGPL/INQUIRY",profile:"QSECOFR");Assert.Equal("*DFT",_policy.Get(submitted.Key));
        Assert.False(session.Execute("CHGJOBD JOBD(QGPL/INQUIRY) INQMSGRPY(*RQD)").IsError);Assert.Equal("*DFT",_policy.Get(submitted.Key));
        Assert.False(session.Execute("RMVRPYLE 10").IsError);Assert.Empty(_entries.List());
    }
    [Theory]
    [InlineData("ADDRPYLE 0")]
    [InlineData("ADDRPYLE 10000")]
    [InlineData("ADDRPYLE 1 MSGID(USR12ZZ)")]
    [InlineData("ADDRPYLE 1 CMPDTA('too long for a twenty eight byte comparison')")]
    [InlineData("ADDRPYLE 1 CMPDTA('A' 1000)")]
    [InlineData("ADDRPYLE 1 CCSID(9999)")]
    [InlineData("ADDRPYLE 1 DUMP(*YES)")]
    [InlineData("CHGRPYLE 1 RPY(Y)")]
    [InlineData("RMVRPYLE 1")]
    [InlineData("CHGJOB INQMSGRPY(*UNKNOWN)")]
    public void Invalid_commands_leave_the_reply_list_and_job_policy_unchanged(string command)
    {
        using var session=new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),CancellationToken.None);
        Assert.True(session.Execute(command).IsError);Assert.Empty(_entries.List());Assert.Equal("*RQD",_policy.Get(session.Job.Key));
    }
    [Fact]
    public void Duplicate_add_and_failing_change_preserve_the_original_entry()
    {
        _entries.Put(new(1,Reply:"Y"));Assert.Throws<CpfException>(()=>_entries.Put(new(1,Reply:"N")));
        Assert.Throws<InvalidOperationException>(()=>_entries.Change(1,_=>throw new InvalidOperationException("change")));
        Assert.Equal("Y",Assert.Single(_entries.List()).Reply);
        _entries.Change(1,old=>old with {Reply="N"});Assert.Equal("N",Assert.Single(_entries.List()).Reply);
    }
    [Fact]
    public void Group_jobs_inherit_current_policy_and_job_end_prevents_further_changes()
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");
        var group=_system.Jobs.CreateGroupJob(job.Key,"CHILD");Assert.Equal("*SYSRPYL",_policy.Get(group.Key));
        _system.Jobs.RequestEnd(group.Key);Assert.Throws<CpfException>(()=>_policy.Set(group.Key,"*DFT"));
    }
    [Fact]
    public void Raw_comparison_bytes_are_not_converted()
    {
        _entries.Put(new(1,"USR1234",CodePage.ToBytes(1208,"BC"),2,65535,"Y"));
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*SYSRPYL");var copy=Send(ccsid:1208);
        Assert.Equal("Y",_queues.Receive(_replies,MessageSelection.Reply,copy)!.Data.ToText());
    }
    [Fact]
    public void Default_policy_answers_immediate_inquiries_with_the_supplied_default()
    {
        var job=_system.Jobs.CreateInteractive("QSECOFR");using var owner=OperationIdentity.Enter("QSECOFR",job.Key);_policy.Set(job.Key,"*DFT");
        var copy=Assert.Single(_queues.SendTo([new(_inbox)],Bytes("immediate"),"INQ",new(_replies),Bytes("N"),senderCopy:true));
        var reply=_queues.Receive(_replies,MessageSelection.Reply,copy)!;Assert.Equal("N",reply.Data.ToText());Assert.Equal("23",reply.ReturnType);
    }
    [Fact]
    public void Copying_a_job_description_preserves_its_inquiry_reply_policy()
    {
        _system.WorkDefinitions.PutJobDescription(new("QGPL","ORIGINAL","QUSRSYS/QBATCH",InquiryMessageReply:"*SYSRPYL"));
        new Ipc.Services.Sqlite.ObjectCatalogOperations(_system.Connections).Relocate(new("QGPL","ORIGINAL"),ObjectType.JobDescription,new("QGPL","COPIED"),copy:true);
        Assert.Equal("*SYSRPYL",_system.WorkDefinitions.JobDescription("QGPL","COPIED")!.InquiryMessageReply);
    }
    public void Dispose()=>_system.Dispose();
}
