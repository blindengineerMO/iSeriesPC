using System.Text;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    private void AutomaticReply(SqliteConnection connection,SqliteTransaction transaction,MessageQueueAddress queue,uint key)
    {
        if(OperationIdentity.Current?.Job is not { } job)return;
        var policy=InquiryReplyPolicy.Read(connection,transaction,job);
        if(policy=="*RQD")return;
        var found=Find(connection,transaction,queue,MessageSelection.Key,key)!;
        if(policy=="*DFT"){ReplyCore(connection,transaction,queue,found,found.DefaultReply,found.DefaultReplyCode);return;}
        if(found.Message.Predefined is not { } predefined)return;
        var entry=SystemReplyList.Match(connection,transaction,found.Message.MessageId,predefined.Replacement);
        if(entry is null || entry.Reply=="*RQD")return;
        if(entry.Reply=="*DFT"){ReplyCore(connection,transaction,queue,found,found.DefaultReply,"25",checkReply:false);return;}
        try
        {
            var encoding=(Encoding)CodePage.FromCcsid(found.Message.Data.Ccsid).Clone();encoding.EncoderFallback=EncoderFallback.ExceptionFallback;
            ReplyCore(connection,transaction,queue,found,new ProgramBuffer(encoding.GetBytes(entry.Reply),found.Message.Data.Ccsid),"25");
        }
        catch(CpfException error)when(error.MessageId=="CPF2422") { /* An invalid automatic reply requires a manual reply. */ }
        catch(EncoderFallbackException) { /* A reply which cannot be represented also requires a manual reply. */ }
    }
}
