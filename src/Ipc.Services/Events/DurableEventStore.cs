using System.Text.Json;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Events;

public sealed record DurableEvent(long Sequence, DateTimeOffset At, string Kind, string Principal, string? Job, string Payload);
public sealed class EventBatch
{
    internal EventBatch(string database, string consumer, long previous, IEnumerable<DurableEvent> events)
    {
        Database = database; Consumer = consumer; PreviousSequence = previous;
        Events = Array.AsReadOnly(events.ToArray());
    }
    internal string Database { get; }
    public string Consumer { get; }
    public long PreviousSequence { get; }
    public IReadOnlyList<DurableEvent> Events { get; }
}

/// <summary>Transactional outbox with explicit durable consumer acknowledgements.</summary>
public sealed class DurableEventStore(SqliteConnectionFactory factory)
{
    public long Append(string kind, object payload, string? principal = null, string? job = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        if (OperationIdentity.Current is { } identity && principal is not null && principal != identity.Principal)
            throw new Ipc.Core.Messages.CpfException("CPF9802", "An authenticated caller cannot override the audit principal.");
        var json = JsonSerializer.Serialize(payload);
        if (json.Length > 65536) throw new ArgumentException("Event payload exceeds 64 KiB.", nameof(payload));
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sys_events(at,kind,principal,job,payload) VALUES($at,$kind,$principal,$job,$payload) RETURNING seq
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$principal", principal ?? OperationIdentity.Current?.Principal ?? "*SYSTEM");
        command.Parameters.AddWithValue("$job", (object?)job ?? OperationIdentity.Current?.Job?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$payload", json);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public EventBatch Read(string consumer, int limit = 100)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Audit);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);
        if (consumer.Length > 128 || limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cursor = connection.CreateCommand();
        cursor.Transaction = transaction;
        cursor.CommandText = "INSERT INTO sys_event_consumers(name) VALUES($name) ON CONFLICT DO NOTHING; SELECT last_seq FROM sys_event_consumers WHERE name=$name";
        cursor.Parameters.AddWithValue("$name", consumer);
        var previous = Convert.ToInt64(cursor.ExecuteScalar());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT seq,at,kind,principal,job,payload FROM sys_events WHERE seq>$seq ORDER BY seq LIMIT $limit";
        command.Parameters.AddWithValue("$seq", previous);
        command.Parameters.AddWithValue("$limit", limit);
        var events = new List<DurableEvent>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) events.Add(new(reader.GetInt64(0), DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        transaction.Commit();
        return new(factory.ConnectionString, consumer, previous, events);
    }

    public void Acknowledge(EventBatch batch)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Audit);
        if (batch.Database != factory.ConnectionString) throw new ArgumentException("Batch belongs to another catalog.", nameof(batch));
        if (batch.Events.Count == 0) return;
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_event_consumers SET last_seq=$next WHERE name=$name AND last_seq=$previous AND $next>$previous";
        command.Parameters.AddWithValue("$name", batch.Consumer);
        command.Parameters.AddWithValue("$previous", batch.PreviousSequence);
        command.Parameters.AddWithValue("$next", batch.Events[^1].Sequence);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Consumer cursor changed; reread before acknowledging.");
    }

    public async Task<int> DeliverAsync(string consumer, Func<DurableEvent, CancellationToken, Task> handler,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var batch = Read(consumer, limit);
        foreach (var item in batch.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await handler(item, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Acknowledge(batch);
        return batch.Events.Count;
    }

    public void RemoveConsumer(string name)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Audit);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sys_event_consumers WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
    }

    public void Retain(DateTimeOffset now, int historyDays = 30, int auditDays = 90)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.Audit);
        if (historyDays < 1 || auditDays < historyDays) throw new ArgumentOutOfRangeException(nameof(historyDays));
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM sys_hst WHERE julianday(at)<julianday($history);
            DELETE FROM sys_joblog WHERE (job_number,job_name,job_user) IN
                (SELECT number,name,user FROM sys_jobs WHERE status IN ('Completed','Ended') AND julianday(completed_at)<julianday($history));
            DELETE FROM sys_events WHERE seq<=coalesce((SELECT min(last_seq) FROM sys_event_consumers),9223372036854775807)
              AND julianday(at)<julianday(CASE WHEN kind LIKE 'security.%' THEN $audit ELSE $history END);
            """;
        command.Parameters.AddWithValue("$history", now.AddDays(-historyDays).ToString("O"));
        command.Parameters.AddWithValue("$audit", now.AddDays(-auditDays).ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
