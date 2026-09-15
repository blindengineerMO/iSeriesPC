using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    public IReadOnlyList<uint> Send(IReadOnlyList<QualifiedName> queues, ProgramBuffer data, string kind = "INFO",
        QualifiedName? replyQueue = null, ProgramBuffer? defaultReply = null, string messageId = "", int severity = 0, CancellationToken cancellationToken = default)
        => SendTo(queues.Select(q => new MessageQueueAddress(q)).ToArray(), data, kind, replyQueue is { } reply ? new(reply) : null, defaultReply, messageId, severity, cancellationToken);
    public QueuedMessage? Receive(QualifiedName queue, MessageSelection selection = MessageSelection.Next, uint key = 0, bool remove = true,
        TimeSpan wait = default, CancellationToken cancellationToken = default, string? kind = null, int? receiveCcsid = null)
        => ReceiveFrom(new(queue), selection, key, remove, wait, cancellationToken, kind, receiveCcsid);
    public uint Reply(QualifiedName queue, uint key, ProgramBuffer? data = null, bool remove = true, CancellationToken cancellationToken = default)
        => ReplyTo(new(queue), key, data, remove, cancellationToken);
    public IReadOnlyList<QueuedMessage> List(QualifiedName queue, uint after = 0, int limit = 100) => ListFrom(new(queue), after, limit);
    public IReadOnlyList<QueuedMessage> ListJobMessages(JobKey job, uint after = 0, int limit = 100)
    {
        if (limit is < 1 or > 4096) throw new CpfException("IPC0003", "Job message page size must be 1–4096.");
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireJob(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.* FROM sys_message_entries e JOIN sys_program_message_queues q ON e.program_queue=q.id
            JOIN sys_jobs j ON j.number=q.job_number
            WHERE j.number=$job AND j.name=$name AND j.user=$user AND e.key>$after ORDER BY e.key LIMIT $limit
            """;
        command.Parameters.AddWithValue("$job", job.Number); command.Parameters.AddWithValue("$name", job.Name); command.Parameters.AddWithValue("$user", job.User);
        command.Parameters.AddWithValue("$after", (long)after); command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader(); var result = new List<QueuedMessage>();
        while (reader.Read()) result.Add(Read(reader).Message); return result;
    }

    public MessageQueueAddress OpenProgramQueue(JobKey job, string program, MessageQueueAddress? parent = null)
    {
        new JobDataAreaStore(factory).RequireOwner(job);
        if (parent is null ? program != "*EXT" : !ObjectName.IsValid(program)) throw new CpfException("IPC0003", "Invalid program queue name.");
        if (parent is not null) AuthorizeProgram(parent);
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.Parameters.AddWithValue("$job", job.Number);
        if (parent is null)
        {
            command.CommandText = "SELECT id FROM sys_program_message_queues WHERE job_number=$job AND external=1 AND active=1";
            if (command.ExecuteScalar() is string existing) { transaction.Commit(); return new(existing); }
        }
        command.CommandText = "SELECT count(*) FROM sys_program_message_queues WHERE job_number=$job AND active=1";
        if ((long)command.ExecuteScalar()! >= 65) throw new CpfException("IPC0126", "Program message queue stack exceeds 64 calls.");
        var id = Guid.NewGuid().ToString("N"); command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$program", program); command.Parameters.AddWithValue("$parent", (object?)parent?.ProgramQueue ?? DBNull.Value);
        command.Parameters.AddWithValue("$external", parent is null ? 1 : 0);
        if (parent is not null)
        {
            command.CommandText = "SELECT count(*) FROM sys_program_message_queues WHERE id=$parent AND job_number=$job AND active=1";
            if ((long)command.ExecuteScalar()! != 1) throw new CpfException("CPF9802", "Parent message queue belongs to another job or has ended.");
        }
        command.CommandText = "INSERT INTO sys_program_message_queues(id,job_number,program,parent,external) VALUES($id,$job,$program,$parent,$external)";
        command.ExecuteNonQuery(); transaction.Commit(); return new(id);
    }
    public void CloseProgramQueue(MessageQueueAddress queue)
    {
        AuthorizeProgram(queue);
        CloseProgramQueueCore(queue);
    }
    internal void CloseProgramQueueForOwner(JobKey job, MessageQueueAddress queue)
    {
        if (OperationIdentity.Current is { } identity && identity.Job != job) throw new CpfException("CPF9802", "Program queue belongs to another job.");
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sys_program_message_queues WHERE id=$id AND job_number=$job";
        command.Parameters.AddWithValue("$id", queue.ProgramQueue!); command.Parameters.AddWithValue("$job", job.Number);
        if ((long)command.ExecuteScalar()! != 1) throw new CpfException("CPF9802", "Program queue belongs to another job.");
        CloseProgramQueueCore(queue);
    }
    private void CloseProgramQueueCore(MessageQueueAddress queue)
    {
        using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = """
            UPDATE sys_program_message_queues SET active=0 WHERE id=$id;
            DELETE FROM sys_program_message_queues WHERE id=$id AND external=0
                AND NOT EXISTS(SELECT 1 FROM sys_message_entries WHERE program_queue=$id)
                AND NOT EXISTS(SELECT 1 FROM sys_program_message_queues WHERE parent=$id);
            """;
        command.Parameters.AddWithValue("$id", queue.ProgramQueue!); command.ExecuteNonQuery(); transaction.Commit();
    }
    public void CloseJobQueues(JobKey job)
    {
        new JobDataAreaStore(factory).RequireOwner(job);
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_program_message_queues SET active=0 WHERE job_number=$job";
        command.Parameters.AddWithValue("$job", job.Number); command.ExecuteNonQuery();
    }
    private void AuthorizeProgram(MessageQueueAddress address, bool replyDelivery = false)
    {
        if (address.ProgramQueue is not { Length: 32 } id || address.Named is not null) throw new CpfException("IPC0003", "Invalid program message queue.");
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT q.job_number,q.active,j.name,j.user,j.status FROM sys_program_message_queues q JOIN sys_jobs j ON j.number=q.job_number WHERE q.id=$id";
        command.Parameters.AddWithValue("$id", id); using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(1) != 1) throw new CpfException("CPF2469", "Program message queue is no longer active.");
        var job = new JobKey(reader.GetInt32(0), reader.GetString(2), reader.GetString(3));
        reader.Close();
        if (!replyDelivery) new JobDataAreaStore(factory).RequireOwner(job);
        else
        {
            // The original inquiry grants only reply delivery to this exact frame.
            // It does not grant its recipient read access to another job's messages.
            using var alive = connection.CreateCommand();
            alive.CommandText = "SELECT count(*) FROM sys_jobs WHERE number=$job AND execution_state='Running' AND cancel_requested=0";
            alive.Parameters.AddWithValue("$job", job.Number);
            if ((long)alive.ExecuteScalar()! != 1) throw new CpfException("CPF2469", "Reply job has ended.");
        }
    }
}
