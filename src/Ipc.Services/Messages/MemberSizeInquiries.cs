using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    internal MessageQueueAddress OpenMemberSizeReplyQueue(JobKey job,CancellationToken token)
    {
        var root=OpenProgramQueue(job,"*EXT");var queue=OpenProgramQueue(job,"QDBSIZE",root);
        try
        {
            using var connection=factory.Open();using var transaction=SqliteCancellation.BeginWrite(connection,token);
            new JobDataAreaStore(factory).RequireOwner(job);using var command=connection.CreateCommand();command.Transaction=transaction;
            command.CommandText="INSERT INTO sys_member_size_waits(reply_queue)VALUES($id)";command.Parameters.AddWithValue("$id",queue.ProgramQueue!);command.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();transaction.Commit();return queue;
        }
        catch{if(!factory.ShutdownToken.IsCancellationRequested)CloseMemberSizeReplyQueue(job,queue);throw;}
    }
    internal void CloseMemberSizeReplyQueue(JobKey job,MessageQueueAddress queue)
    {
        if(OperationIdentity.Current is { } identity&&identity.Job!=job)throw new CpfException("CPF9802","Member inquiry belongs to another job.");
        using var connection=factory.Open();using var transaction=connection.BeginTransaction(deferred:false);
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.Parameters.AddWithValue("$id",queue.ProgramQueue!);command.Parameters.AddWithValue("$job",job.Number);command.Parameters.AddWithValue("$name",job.Name);command.Parameters.AddWithValue("$user",job.User);
        command.CommandText="SELECT count(*) FROM sys_program_message_queues q JOIN sys_jobs j ON j.number=q.job_number WHERE q.id=$id AND q.program='QDBSIZE' AND j.number=$job AND j.name=$name AND j.user=$user";
        if(Convert.ToInt64(command.ExecuteScalar())==0){transaction.Commit();return;}
        command.CommandText="UPDATE sys_message_entries SET replied=1 WHERE kind='INQ' AND sender_job=$job AND reply_program_queue=$id; DELETE FROM sys_program_message_queues WHERE id=$id";
        command.ExecuteNonQuery();transaction.Commit();
    }
}
