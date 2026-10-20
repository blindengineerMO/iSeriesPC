using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Messages;

internal static class InquiryReplyFixture
{
    internal static void Seed(IpcSystem system,SqliteFileStore files)
    {
        var descriptions=new MessageDescriptionStore(system.Connections);descriptions.Create("QGPL","GINQMSG");
        descriptions.Add("QGPL","GINQMSG","USR1234",new MessageDescription("Continue &1?","",0,[new("*CHAR",4)],37,"N",ReplyRules:new("*CHAR",1,Values:["Y","N"])));
        files.SaveSourceMember("QGPL","SOURCE","CLINQRPY","""
            PGM
            DCL &KEY *CHAR LEN(4)
            DCL &REPLY *CHAR LEN(8)
            DCL &TYPE *CHAR LEN(2)
            ADDRPYLE SEQNBR(8100) MSGID(USR1234) CMPDTA('BC' 2) RPY(Y)
            CHGJOB INQMSGRPY(*SYSRPYL)
            SNDPGMMSG MSGID(USR1234) MSGF(QGPL/GINQMSG) MSGDTA('ABCD') MSGTYPE(*INQ) TOMSGQ(QSYS/QSYSOPR) KEYVAR(&KEY)
            RCVMSG MSGTYPE(*RPY) MSGKEY(&KEY) MSG(&REPLY) RTNTYPE(&TYPE)
            CHGJOB INQMSGRPY(*RQD)
            RMVRPYLE SEQNBR(8100)
            IF COND(&REPLY *EQ 'Y' *AND &TYPE *EQ '25') THEN(SNDPGMMSG MSG('SYSTEM REPLY LIST ACCEPTED'))
            ENDPGM
            """,null);
    }
}
