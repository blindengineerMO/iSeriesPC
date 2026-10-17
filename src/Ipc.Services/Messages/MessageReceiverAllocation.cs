using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Messages;

public sealed partial class MessageQueueStore
{
    public IDisposable AllocateReceiver(MessageQueueAddress queue, JobKey job, CancellationToken cancellationToken = default)
    {
        queue = Resolve(queue);
        if (queue.Named is not { } named) throw Invalid("Receiver allocation requires a named queue.");
        new JobDataAreaStore(factory).RequireOwner(job); _ = Authorize(queue, Authorities.UseBits);
        var allocation = new JobLockStore(factory).AcquireWithLifetime(job, new(named.Library, named.Name.Value, ObjectType.MessageQueue),
            JobLockMode.SharedRead, TimeSpan.Zero, cancellationToken, "MessageReceive", Guid.NewGuid().ToString("N"));
        try
        {
            var token = Guid.NewGuid().ToString("N");
            using var connection = factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var insert = connection.CreateCommand(); insert.Transaction = transaction;
            RequireReceiverAvailable(connection, transaction, queue, includeOwner: true);
            insert.CommandText = "INSERT INTO sys_message_receivers(lib,name,job_number,token) VALUES($lib,$name,$job,$token)";
            insert.Parameters.AddWithValue("$lib", named.Library); insert.Parameters.AddWithValue("$name", named.Name.Value);
            insert.Parameters.AddWithValue("$job", job.Number); insert.Parameters.AddWithValue("$token", token);
            cancellationToken.ThrowIfCancellationRequested(); insert.ExecuteNonQuery(); transaction.Commit();
            return new ReceiverLease(this, token, allocation);
        }
        catch { allocation.Dispose(); throw; }
    }
    private static void RequireReceiverAvailable(SqliteConnection connection, SqliteTransaction transaction, MessageQueueAddress queue, bool includeOwner = false)
    {
        if (queue.Named is not { } named) return;
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = "SELECT job_number FROM sys_message_receivers WHERE lib=$lib AND name=$name";
        query.Parameters.AddWithValue("$lib", named.Library); query.Parameters.AddWithValue("$name", named.Name.Value);
        if (query.ExecuteScalar() is long owner && (includeOwner || OperationIdentity.Current is { } identity && identity.Job?.Number != owner))
            throw new CpfException("CPF2451", "Message queue is allocated to a waiting receiver.");
    }
    private void ReleaseReceiver(string token)
    {
        using var connection = factory.Open(); using var remove = connection.CreateCommand();
        remove.CommandText = "DELETE FROM sys_message_receivers WHERE token=$token"; remove.Parameters.AddWithValue("$token", token); remove.ExecuteNonQuery();
    }
    private sealed class ReceiverLease(MessageQueueStore owner, string token, IDisposable allocation) : IDisposable
    {
        private bool _ended;
        public void Dispose()
        {
            if (_ended) return; _ended = true;
            try
            {
                owner.ReleaseReceiver(token);
            }
            finally { allocation.Dispose(); }
        }
    }
}
