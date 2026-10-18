using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Db.Store;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Fact]
    public void RETURN_retains_an_update_read_and_LR_releases_it_for_another_job()
    {
        Compile(UpdateFile() + "dcl-s CALLS int(10);\neval CALLS=CALLS+1;\nif CALLS=1;\nchain 20 DATA;\nsndpgmmsg 'LOCKED';\nreturn;\nendif;\n" +
            "eval TEXT='retained';\nupdate ROW;\nseton LR;", group: "UPDFILES");
        using var first = Session(); using var second = Session(); Ok(first.Execute("CALL QGPL/APP"), "LOCKED");
        using (OperationIdentity.Enter("QSECOFR", second.Job.Key))
        {
            using var contender = _files.OpenUpdateCursor("QGPL", "DATA", "DATA");
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => contender.ChainRecord(1)).MessageId);
            using var inquiry = _files.OpenSequentialCursor("QGPL", "DATA", "DATA"); Assert.True(inquiry.ChainRecord(1));
        }
        var result = first.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Equal("retained", Text(101)); Assert.Empty(Locks());
    }
    [Fact]
    public void N_input_can_read_another_programs_locked_record_without_releasing_that_programs_lock()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nreturn;", "OWNER", "UPDATES");
        Compile(UpdateFile() + "chain(n) 20 DATA;\nsndpgmmsg %char(ID);\nseton LR;", "INQUIRY");
        using var first = Session(); using var second = Session(); Assert.False(first.Execute("CALL QGPL/OWNER").IsError);
        Ok(second.Execute("CALL QGPL/INQUIRY"), "101"); Assert.Contains(Locks(), l => l.Lifetime == "UpdateRead" && l.Job == first.Job.Key);
        Assert.False(first.Execute("RCLACTGRP ACTGRP(UPDATES)").IsError); Assert.Empty(Locks());
    }
    [Fact]
    public void Independent_update_programs_in_one_job_contend_until_UNLOCK()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nreturn;", "FIRST", "UPDATES");
        Compile(UpdateFile() + "chain 20 DATA;\nseton LR;", "SECOND");
        using var session = Session(); Assert.False(session.Execute("CALL QGPL/FIRST").IsError);
        var blocked = session.Execute("CALL QGPL/SECOND"); Assert.True(blocked.IsError); Assert.Contains("CPF1002", blocked.Message);
        Assert.False(session.Execute("RCLACTGRP ACTGRP(UPDATES)").IsError); Assert.False(session.Execute("CALL QGPL/SECOND").IsError);
    }
    [Fact]
    public void SHARE_preserves_one_update_input_scope_for_compatible_programs()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nreturn;", "FIRST", "UPDATES");
        Compile(UpdateFile() + "chain 20 DATA;\neval TEXT='shared';\nupdate ROW;\nseton LR;", "SECOND");
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/DATA) SHARE(*YES) OVRSCOPE(*JOB)").IsError);
        Assert.False(session.Execute("CALL QGPL/FIRST").IsError); Assert.False(session.Execute("CALL QGPL/SECOND").IsError); Assert.Equal("shared", Text(101));
        Assert.False(session.Execute("RCLACTGRP ACTGRP(UPDATES)").IsError); Assert.Empty(Locks());
    }
    [Fact]
    public void Inquiry_and_update_programs_never_share_an_incompatible_open_mode()
    {
        Compile(UpdateFile() + "chain 20 DATA;\nreturn;", "OWNER", "UPDATES");
        Compile("**free\ndcl-f DATA keyed;\nchain 20 DATA;\nsndpgmmsg %char(ID);\nseton LR;", "INPUT");
        using var session = Session(); Assert.False(session.Execute("OVRDBF FILE(DATA) TOFILE(QGPL/DATA) SHARE(*YES) OVRSCOPE(*JOB)").IsError);
        Assert.False(session.Execute("CALL QGPL/OWNER").IsError); Ok(session.Execute("CALL QGPL/INPUT"), "101");
        Assert.Single(Locks(), l => l.Lifetime == "UpdateRead"); Assert.False(session.Execute("RCLACTGRP ACTGRP(UPDATES)").IsError);
    }
    [Fact]
    public void Declared_update_open_requires_live_update_authority()
    {
        Compile(UpdateFile() + "read DATA;\nseton LR;"); _system.Security.Profiles.Create(new() { Name = "INPUTUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("INPUTUSER"), "InputUserUpdateFixture42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "INPUTUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "INPUTUSER", Authorities.UseBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("INPUTUSER"), CancellationToken.None);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message); Assert.Empty(Locks());
    }
    [Fact]
    public void Delete_usage_requires_delete_authority_even_before_an_input_operation()
    {
        Compile(UpdateFile("*DELETE") + "read DATA;\nseton LR;"); _system.Security.Profiles.Create(new() { Name = "UPDUSER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("UPDUSER"), "DeleteAccessFixture42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "UPDUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "UPDUSER", Authorities.UseBits | AuthorityBit.Update);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("UPDUSER"), CancellationToken.None);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message); Assert.Empty(Locks());
    }
    [Fact]
    public void Update_programs_recheck_authority_on_a_retained_call_before_mutation()
    {
        Compile(UpdateFile() + "dcl-s CALLS int(10);\neval CALLS=CALLS+1;\nif CALLS=1;\nchain 20 DATA;\nreturn;\nendif;\neval TEXT='denied';\nupdate ROW;\nseton LR;", group: "UPDATES");
        _system.Security.Profiles.Create(new() { Name = "UPDUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("UPDUSER"), "RevokedUpdateFixture42");
        _system.Security.Authority.Grant("QGPL", "APP", ObjectType.Program, "UPDUSER", Authorities.UseBits);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "UPDUSER", Authorities.ChangeBits);
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("UPDUSER"), CancellationToken.None);
        Assert.False(session.Execute("CALL QGPL/APP").IsError);
        _system.Security.Authority.Grant("QGPL", "DATA", ObjectType.File, "UPDUSER", Authorities.UseBits);
        var denied = session.Execute("CALL QGPL/APP"); Assert.True(denied.IsError); Assert.Contains("CPF9802", denied.Message); Assert.Equal("first", Text(101));
    }
    [Fact]
    public void Missing_input_does_not_replace_program_fields_or_leave_an_update_target()
    {
        var program = Parse(UpdateFile() + "chain 20 DATA;\nchain 999 DATA;\nif not %found(DATA) and ID=101;\nsndpgmmsg 'UNCHANGED';\nendif;\nupdate ROW;\nseton LR;");
        using var session = Session(); using var owner = OperationIdentity.Enter("QSECOFR", session.Job.Key);
        var messages = new List<string>();
        var host = new RpgHost { Files = new RpgSqliteFileAccess(_files), LibraryResolver = (_, _) => "QGPL", SendMessage = messages.Add };
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program)); Assert.Contains("UNCHANGED", messages);
        Assert.Equal("first", Text(101)); Assert.Empty(Locks());
    }
    [Fact]
    public void Custom_hosts_cannot_substitute_an_input_cursor_for_an_update_binding()
    {
        var program = Parse(UpdateFile() + "read DATA;\nseton LR;");
        var host = new RpgHost { OpenDisk = _ => { var cursor = new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "DATA", "DATA", keyed: true); return new(() => cursor, cursor.Dispose); } };
        Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program));
    }
}
