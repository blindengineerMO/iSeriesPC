using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class MemberCapacityRecoveryTests
{
    private Job InitializationAutomatic()
    {
        new SystemReplyList(_system.Connections).Put(new(2,"CPA3138",Reply:"I"));var job=Job();new InquiryReplyPolicy(_system.Connections).Set(job.Key,"*SYSRPYL");return job;
    }
    private async Task<QueuedMessage> InitializationInquiry()
    {
        var deadline=DateTime.UtcNow.AddSeconds(10);
        while(true){var entry=_messages.List(_operator).FirstOrDefault(m=>m.MessageId=="CPA3138"&&!m.Replied);if(entry is not null)return entry;Assert.True(DateTime.UtcNow<deadline,"Expected INZPFM inquiry.");await Task.Delay(20);}
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void INZPFM_uses_its_own_inquiry_and_preserves_prefix_records_and_deleted_gaps(bool deleted)
    {
        using(var cursor=_files.OpenSequentialCursor("QGPL","DATA","DATA",keyed:false)){Assert.True(cursor.DeleteRecord(2));Assert.True(cursor.DeleteRecord(3));}
        var job=InitializationAutomatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        Assert.Equal(2,_files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:deleted));Assert.Equal(5L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        using var verify=_files.OpenSequentialCursor("QGPL","DATA","DATA",keyed:false);Assert.True(verify.ChainRecord(1));Assert.Equal(1L,verify.Current!["ID"]);Assert.False(verify.ChainRecord(2));Assert.False(verify.ChainRecord(3));
        Assert.Equal(!deleted,verify.ChainRecord(4));if(!deleted)Assert.Equal(0L,verify.Current!["ID"]);
        Assert.Equal(deleted?1:3,_files.RowCount("QGPL","DATA","DATA"));var inquiry=Assert.Single(_messages.List(_operator));Assert.Equal("CPA3138",inquiry.MessageId);Assert.True(inquiry.Replied);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void INZPFM_default_reply_cancels_with_CPF3140_without_changing_records_extent_or_capacity(bool deleted)
    {
        var job=Job();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);new InquiryReplyPolicy(_system.Connections).Set(job.Key,"*DFT");
        Assert.Equal("CPF3140",Assert.Throws<CpfException>(()=>_files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:deleted)).MessageId);
        Assert.Equal(3,_files.RowCount("QGPL","DATA","DATA"));Assert.Equal(3L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));
    }
    [Fact]
    public void INZPFM_repeated_I_replies_grant_single_increments_until_the_original_total_is_reached()
    {
        var job=InitializationAutomatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        Assert.Equal(6,_files.InitializeMember("QGPL","DATA",totalRecords:9,deleted:true));Assert.Equal(9L,Scalar("SELECT capacity FROM sys_member_capacity"));Assert.Equal(3,_messages.List(_operator).Count);Assert.All(_messages.List(_operator),m=>Assert.Equal("CPA3138",m.MessageId));
    }
    [Fact]
    public async Task INZPFM_rejects_numeric_multi_increment_replies_but_allows_a_corrected_I_reply()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return _files.InitializeMember("QGPL","DATA",totalRecords:5,deleted:true,cancellationToken:cancellation.Token);});
        try
        {
            var inquiry=await InitializationInquiry();foreach(var value in new[]{"1","9999","bad"})Assert.Equal("CPF2422",Assert.Throws<CpfException>(()=>_messages.Reply(_operator,inquiry.Key,Reply(value))).MessageId);
            Assert.False(work.IsCompleted);_messages.Reply(_operator,inquiry.Key,Reply("I"));Assert.Equal(2,await work.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally{cancellation.Cancel();try{await work.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}}
    }
    [Fact]
    public async Task INZPFM_next_increment_does_not_advance_twice_if_another_job_reaches_its_target_while_waiting()
    {
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return _files.InitializeMember("QGPL","DATA",deleted:true,cancellationToken:cancellation.Token);});
        try
        {
            var inquiry=await InitializationInquiry();var other=Automatic();using(OperationIdentity.Enter("QSECOFR",other.Key)){Add(4);Add(5);}
            _messages.Reply(_operator,inquiry.Key,Reply("I"),remove:false);Assert.Equal(0,await work.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(5L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));Assert.Single(_messages.List(_operator),m=>m.MessageId=="CPA3138");
        }
        finally{cancellation.Cancel();try{await work.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}}
    }
    [Fact]
    public async Task INZPFM_LAST_stays_bound_to_the_selected_member_when_a_later_member_is_added()
    {
        _files.ChangePhysicalMemberLimit("QGPL","DATA",4);_files.AddMember("QGPL","DATA","OLD");_files.InitializeMember("QGPL","DATA","OLD",3,true);
        var job=Job();using var cancellation=new CancellationTokenSource();var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return _files.InitializeMember("QGPL","DATA","*LAST",5,true,cancellation.Token);});
        try
        {
            var inquiry=await InitializationInquiry();_files.AddMember("QGPL","DATA","NEW");_files.InitializeMember("QGPL","DATA","NEW",1,true);
            _messages.Reply(_operator,inquiry.Key,Reply("I"));Assert.Equal(2,await work.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(5L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA' AND mbr='OLD'"));Assert.Equal(1L,Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA' AND mbr='NEW'"));
            Assert.Equal("DATA      OLD       ",inquiry.Predefined!.Replacement.ToText()[..20]);
        }
        finally{cancellation.Cancel();try{await work.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){}}
    }
    [Fact]
    public void INZPFM_materialization_quotas_are_checked_before_any_operator_inquiry_or_grant()
    {
        var job=InitializationAutomatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        Assert.Equal("IPC0139",Assert.Throws<CpfException>(()=>_files.InitializeMember("QGPL","DATA",totalRecords:1000004)).MessageId);Assert.Empty(_messages.List(_operator));Assert.Equal(0L,Scalar("SELECT count(*) FROM sys_member_capacity"));
    }
    [Theory]
    [InlineData(null)] [InlineData(5L)]
    public void INZPFM_zero_increments_report_CPF3143_before_asking_for_an_impossible_extension(long? total)
    {
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,memberSize:new(3,0,0));var job=InitializationAutomatic();using var owner=OperationIdentity.Enter("QSECOFR",job.Key);
        Assert.Equal("CPF3143",Assert.Throws<CpfException>(()=>_files.InitializeMember("QGPL","DATA",totalRecords:total,deleted:true)).MessageId);Assert.Empty(_messages.List(_operator));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task INZPFM_cancellation_and_operator_cancel_leave_no_partial_initialization(bool cancelToken)
    {
        var job = Job();
        using var cancellation = new CancellationTokenSource();
        var work = Task.Run(() => {
            using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
            return Record.Exception(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 5, cancellationToken: cancellation.Token));
        });
        try
        {
            var inquiry = await InitializationInquiry();
            // A different member can acquire the SQLite writer while the inquiry waits.
            _files.InitializeMember("QGPL", "OTHER", totalRecords: 2, deleted: true);
            if (cancelToken) cancellation.Cancel();
            else _messages.Reply(_operator, inquiry.Key, Reply("C"));
            var error = await work.WaitAsync(TimeSpan.FromSeconds(10));
            if (cancelToken) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.Equal("CPF3140", Assert.IsType<CpfException>(error).MessageId);
            Assert.Equal(JobStatus.Active, _system.Jobs.GetRequired(job.Key).Status);
            Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
            Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_capacity"));
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_size_waits"));
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_program_message_queues WHERE program='QDBSIZE'"));
            Assert.All(_messages.List(_operator), message => Assert.True(message.Replied));
        }
        finally { cancellation.Cancel(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task INZPFM_rechecks_management_and_deleted_authority_before_granting_capacity(bool revokeDelete)
    {
        _system.Security.Profiles.Create(new() { Name = "INITUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("INITUSER"), "InitializationUserFixture42");
        Sql("UPDATE sys_objects SET public_authority=1 WHERE lib='QGPL' AND name='DATA' AND type='*FILE'");
        var permissions = AuthorityBit.ObjectOperate | AuthorityBit.Add | AuthorityBit.ObjectManagement | AuthorityBit.Delete;
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITUSER", permissions);
        var job = _system.Jobs.CreateInteractive("INITUSER");
        using var cancellation = new CancellationTokenSource();
        var work = Task.Run(() => {
            using var owner = OperationIdentity.Enter("INITUSER", job.Key);
            return Record.Exception(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 5, deleted: true, cancellationToken: cancellation.Token));
        });
        try
        {
            var inquiry = await InitializationInquiry();
            _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INITUSER", permissions & ~(revokeDelete ? AuthorityBit.Delete : AuthorityBit.ObjectManagement));
            _messages.Reply(_operator, inquiry.Key, Reply("I"));
            Assert.Equal("CPF9802", Assert.IsType<CpfException>(await work.WaitAsync(TimeSpan.FromSeconds(10))).MessageId);
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_capacity"));
            Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        }
        finally { cancellation.Cancel(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task INZPFM_recreated_member_rejects_the_original_capacity_reply()
    {
        var job = Job();
        using var cancellation = new CancellationTokenSource();
        var work = Task.Run(() => {
            using var owner = OperationIdentity.Enter("QSECOFR", job.Key);
            return Record.Exception(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 5, deleted: true, cancellationToken: cancellation.Token));
        });
        try
        {
            var inquiry = await InitializationInquiry();
            _files.RemoveMember("QGPL", "DATA", "DATA"); _files.AddMember("QGPL", "DATA", "DATA");
            _messages.Reply(_operator, inquiry.Key, Reply("I"));
            Assert.IsType<CpfException>(await work.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_member_capacity"));
            Assert.Equal(0L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        }
        finally { cancellation.Cancel(); await work.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void INZPFM_multiple_approvals_capture_one_recoverable_initialization_and_preserve_capacity(bool deleted, bool owned)
    {
        var journal = new QualifiedName("QGPL", "INITJRN");
        _system.Journals.CreateReceiver(new("QGPL", "INITRCV"));
        _system.Journals.Create(journal, new("QGPL", "INITRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), journal, "*BOTH");
        var job = InitializationAutomatic();
        using (OperationIdentity.Enter("QSECOFR", job.Key)) Assert.Equal(6, _files.InitializeMember("QGPL", "DATA", totalRecords: 9, deleted: deleted));
        var entry = Assert.Single(_system.Journals.Read(journal));
        Assert.Equal("F", entry.Code); Assert.Equal("IZ", entry.Type); Assert.Equal(job.Key.ToString(), entry.Job);
        using var recoveryIdentity = owned ? OperationIdentity.Enter("QSECOFR", job.Key) : null;
        var selection = new[] { new Ipc.Services.Work.JournalFileSelection(new("QGPL", "DATA"), "DATA") };
        Assert.Equal(1, _system.Journals.Recover(journal, selection, 1, 1, remove: true).Changes);
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(9L, Scalar("SELECT capacity FROM sys_member_capacity"));
        Assert.Equal(1, _system.Journals.Recover(journal, selection, 1, 1).Changes);
        Assert.Equal(deleted ? 3 : 9, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(9L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        Assert.Equal(9L, Scalar("SELECT capacity FROM sys_member_capacity"));
    }

    [Theory]
    [InlineData(37, false)]
    [InlineData(1208, false)]
    [InlineData(37, true)]
    [InlineData(1208, true)]
    public void Compiled_CL_INZPFM_handles_operator_approval_or_monitors_CPF3140(int ccsid, bool cancel)
    {
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        var source = cancel ? """
            PGM
            CHGJOB INQMSGRPY(*DFT)
            INZPFM FILE(QGPL/DATA) TOTRCDS(5)
            MONMSG MSGID(CPF3140) EXEC(SNDPGMMSG MSG('INITIALIZATION CANCELED'))
            ENDPGM
            """ : """
            PGM
            ADDRPYLE SEQNBR(10) MSGID(CPA3138) CMPDTA(DATA 1) RPY(I)
            CHGJOB INQMSGRPY(*SYSRPYL)
            INZPFM FILE(QGPL/DATA) TOTRCDS(5)
            RMVRPYLE SEQNBR(10)
            SNDPGMMSG MSG('INITIALIZATION APPROVED')
            ENDPGM
            """;
        _files.SaveSourceMember("QGPL", "SOURCE", "INITCAP", source, null);
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var compiled = session.Execute("CRTCLPGM PGM(QGPL/INITCAP) SRCFILE(QGPL/SOURCE)");
        Assert.False(compiled.IsError, compiled.Message);
        _files.RemoveMember("QGPL", "SOURCE", "INITCAP");
        var result = session.Execute("CALL QGPL/INITCAP");
        Assert.False(result.IsError, result.Message);
        Assert.Contains(cancel ? "INITIALIZATION CANCELED" : "INITIALIZATION APPROVED", result.Message);
        Assert.Equal(cancel ? 3 : 5, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(cancel ? 0L : 1L, Scalar("SELECT count(*) FROM sys_member_capacity"));
    }

    [Fact]
    public void INZPFM_failed_journal_capture_rolls_back_records_but_keeps_previously_approved_capacity()
    {
        _system.Journals.CreateReceiver(new("QGPL", "FAILRCV"));
        _system.Journals.Create(new("QGPL", "FAILJRN"), new("QGPL", "FAILRCV"));
        _system.Journals.StartFile(new("QGPL", "DATA"), new("QGPL", "FAILJRN"), "*BOTH");
        Sql("CREATE TRIGGER reject_init_capture BEFORE INSERT ON sys_journal_entries WHEN NEW.entry_type='IZ' BEGIN SELECT RAISE(ABORT,'reject initialization'); END");
        var job = InitializationAutomatic();
        using (OperationIdentity.Enter("QSECOFR", job.Key))
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _files.InitializeMember("QGPL", "DATA", totalRecords: 5));
        Assert.Equal(3, _files.RowCount("QGPL", "DATA", "DATA"));
        Assert.Equal(3L, Scalar("SELECT high_water FROM sys_record_slots WHERE name='DATA'"));
        Assert.Equal(5L, Scalar("SELECT capacity FROM sys_member_capacity"));
        Assert.Empty(_system.Journals.Read(new("QGPL", "FAILJRN")));
        Sql("DROP TRIGGER reject_init_capture");
        Assert.Equal(2, _files.InitializeMember("QGPL", "DATA", totalRecords: 5));
        Assert.Single(_messages.List(_operator));
    }

    [Fact]
    public void Compiled_CL_can_read_approved_defaults_after_monitoring_a_later_canceled_initialization()
    {
        var format = new Ipc.Db.Definitions.RecordFormat { Name = "FLOWROW", Fields = [new() { Name = "ID", Type = Ipc.Db.Definitions.FieldType.Binary, Length = 4 }] };
        format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "FLOW", new() { Name = "FLOW", Attribute = Ipc.Db.Definitions.FileAttribute.Physical, Formats = [format], MemberSize = new(1, 2, 1) }, "");
        for (var id = 1; id <= 3; id++) _files.Insert("QGPL", "FLOW", "FLOW", "FLOWROW", new Dictionary<string, object?> { ["ID"] = id });
        _files.CreateSourceFile("QGPL", "SOURCE", sourceWidth: 240);
        _files.SaveSourceMember("QGPL", "SOURCE", "INITFLOW", """
            PGM
            DCL &CANCELED *CHAR LEN(1) VALUE(N)
            DCLF FILE(QGPL/FLOW)
            ADDRPYLE SEQNBR(10) MSGID(CPA3138) CMPDTA(FLOW 1) RPY(I)
            CHGJOB INQMSGRPY(*SYSRPYL)
            INZPFM FILE(QGPL/FLOW) TOTRCDS(5)
            CHGJOB INQMSGRPY(*DFT)
            INZPFM FILE(QGPL/FLOW) TOTRCDS(7)
            MONMSG MSGID(CPF3140) EXEC(CHGVAR VAR(&CANCELED) VALUE(Y))
            CHGJOB INQMSGRPY(*RQD)
            RMVRPYLE SEQNBR(10)
            RCVF
            RCVF
            RCVF
            RCVF
            RCVF
            IF COND(&ID *EQ 0 *AND &CANCELED *EQ 'Y') THEN(SNDPGMMSG MSG('FLOW APPROVED'))
            ENDPGM
            """, null);
        var job = Job();
        using var session = new Ipc.Session.ExecutionSession(_system, job, CancellationToken.None);
        var compiled = session.Execute("CRTCLPGM PGM(QGPL/INITFLOW) SRCFILE(QGPL/SOURCE)");
        Assert.False(compiled.IsError, compiled.Message);
        var result = session.Execute("CALL QGPL/INITFLOW");
        Assert.False(result.IsError, result.Message);
        Assert.Contains("FLOW APPROVED", result.Message + " | " + string.Join(" | ", _messages.ListJobMessages(job.Key).Select(message => message.Data.ToText())));
    }
}
