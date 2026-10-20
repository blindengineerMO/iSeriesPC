using Ipc.Core.Objects;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Compiled_RPG_WRITE_resumes_after_a_system_reply_list_capacity_grant(int ccsid)
    {
        _files.ChangePhysicalMemberLimit("QGPL","DATA",null,memberSize:new(1,2,1));
        new SystemReplyList(_system.Connections).Put(new(1,"CPA5305",Reply:"I"));
        Compile("**free\ndcl-f DATA usage(*output);\neval ID=104;\neval KEY=40;\neval TEXT='capacity';\nwrite ROW;\nsndpgmmsg 'RPG CAPACITY OK';\nseton LR;");
        _files.RemoveMember("QGPL","SOURCE","APP");using var session=Session(ccsid);Assert.False(session.Execute("CHGJOB INQMSGRPY(*SYSRPYL)").IsError);
        Ok(session.Execute("CALL QGPL/APP"),"RPG CAPACITY OK");Assert.Equal("capacity",Text(104));Assert.Equal(4,Count);
        var message=Assert.Single(new MessageQueueStore(_system.Connections).List(new QualifiedName("QSYS","QSYSOPR")));Assert.True(message.Replied);Assert.Equal("CPA5305",message.MessageId);
    }
}
