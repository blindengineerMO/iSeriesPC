using System.Collections.Concurrent;
using System.Diagnostics;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Job-owned staged record units. Only Publish writes the live records and journals.</summary>
public sealed partial class JobRecordTransactions(SqliteConnectionFactory factory) : IDisposable
{
    private static readonly ConcurrentDictionary<(string Database, JobKey Job), RecordUnit> Live = new();
    private readonly ConcurrentDictionary<JobKey, RecordUnit> _owned = new();
    private readonly object _lifecycle = new();
    private volatile bool _disposed;
    private string Database => factory.DatabasePath ?? factory.ConnectionString;
    public void Begin(JobKey job)
    { lock (_lifecycle) BeginCore(job); }
    private void BeginCore(JobKey job)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); RequireOwner(job);
        var unit = new RecordUnit(factory, job);
        try
        {
            using var connection = factory.Locks.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO record_transactions(job_number,job_name,job_user,owner,pid,started) VALUES($number,$name,$user,$owner,$pid,$started)";
            BindJob(command, job); command.Parameters.AddWithValue("$owner", unit.Id); command.Parameters.AddWithValue("$pid", Environment.ProcessId); command.Parameters.AddWithValue("$started", Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);
            try { command.ExecuteNonQuery(); } catch (SqliteException error) when (error.SqliteErrorCode == 19) { throw Invalid("A record transaction definition already exists for this job."); }
            if (!Live.TryAdd((Database, job), unit)) throw Invalid("A record transaction definition already exists for this job.");
            transaction.Commit(); _owned[job] = unit;
        }
        catch { unit.Dispose(); Live.TryRemove(new KeyValuePair<(string, JobKey), RecordUnit>((Database, job), unit)); throw; }
    }
    public void Enlist(JobKey job, QualifiedName file)
    { RequireOwner(job); var unit = Required(job); lock (unit.Gate) unit.Enlist(file); }
    public RecordUnitInfo Inspect(JobKey job)
    { RequireOwner(job); var unit = Required(job); lock (unit.Gate) return new(unit.Id, unit.Cycle, unit.PendingCount(), unit.Enlisted.ToArray()); }
    public RecordOperation Open(JobKey job, CancellationToken cancellationToken = default)
    {
        RequireOwner(job); var unit = Required(job); EnterGate(unit, cancellationToken);
        try { return unit.Open(cancellationToken); } catch { Monitor.Exit(unit.Gate); throw; }
    }
    public void Commit(JobKey job, CancellationToken cancellationToken = default)
    {
        RequireOwner(job); var unit = Required(job); EnterGate(unit, cancellationToken);
        try { unit.Publish(cancellationToken); } finally { Monitor.Exit(unit.Gate); }
    }
    public void Rollback(JobKey job)
    { RequireOwner(job); var unit = Required(job); lock (unit.Gate) unit.RollbackWithCursors(); }
    public void End(JobKey job)
    {
        RequireOwner(job); var unit = Required(job);
        lock (unit.Gate) { unit.RequireIdle(); unit.RequireClosedCursors(); if (unit.PendingCount() != 0) throw Invalid("Commit or roll back pending changes before ending the record unit."); Remove(job, unit); }
    }
    internal bool IsEnlisted(QualifiedName file) => OperationIdentity.Current?.Job is { } job && Find(job) is { } unit && unit.Enlisted.Contains(file);
    internal bool HasCurrentDefinition => OperationIdentity.Current?.Job is { } job && Find(job) is not null;
    internal bool OwnsPendingRecord(JobLockResource record) => OperationIdentity.Current?.Job is { } job && Find(job) is { } unit && unit.OwnsRecord(record);
    internal RecordOperation? OpenEnlisted(IReadOnlyList<QualifiedName> files, CancellationToken cancellationToken = default, IReadOnlyList<string>? memberNames = null)
    {
        if (OperationIdentity.Current?.Job is not { } job) return null;
        var unit = Find(job); if (unit is null || !files.Any(unit.Enlisted.Contains)) return null;
        RequireOwner(job); EnterGate(unit, cancellationToken);
        try { foreach (var file in files) unit.Enlist(file); return unit.Open(cancellationToken, files, memberNames); } catch { Monitor.Exit(unit.Gate); throw; }
    }
    internal void AbortJob(JobKey job)
    {
        if (Live.TryGetValue((Database, job), out var unit)) lock (unit.Gate) Remove(job, unit);
        else
        {
            using var connection = factory.Locks.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT owner,pid,started FROM record_transactions WHERE job_number=$number AND job_name=$name AND job_user=$user"; BindJob(command, job);
            string? owner = null; var alive = false;
            using (var reader = command.ExecuteReader())
                if (reader.Read())
                {
                    owner = reader.GetString(0);
                    try { using var process = Process.GetProcessById(reader.GetInt32(1)); alive = !process.HasExited && process.StartTime.ToUniversalTime().Ticks == reader.GetInt64(2); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    catch (global::System.ComponentModel.Win32Exception) { alive = true; }
                }
            command.CommandText = "DELETE FROM record_transactions WHERE job_number=$number AND job_name=$name AND job_user=$user"; command.ExecuteNonQuery();
            if (!alive && owner is not null && Guid.TryParseExact(owner, "N", out _) && !factory.UsesMemoryDatabase)
                try { Directory.Delete(Path.Combine(factory.DataDirectory, ".record-units", owner), true); } catch (IOException) { }
        }
    }
    private RecordUnit? Find(JobKey job)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Live.TryGetValue((Database, job), out var unit)) return unit;
        using var connection = factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM record_transactions WHERE job_number=$number AND job_name=$name AND job_user=$user"; BindJob(command, job);
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw Invalid("This job's record unit belongs to another process; execute it through its owning session.");
        return null;
    }
    private RecordUnit Required(JobKey job) => Find(job) ?? throw Invalid("No record transaction definition exists for this job.");
    private static void EnterGate(RecordUnit unit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(unit.Gate, 50)) cancellationToken.ThrowIfCancellationRequested();
    }
    private void RequireOwner(JobKey job)
    {
        if (OperationIdentity.Current?.Job is { } current && current != job) throw new CpfException("CPF9802", "A record unit belongs to its executing job.");
        new JobDataAreaStore(factory).RequireOwner(job);
        var active = new JobService(factory).GetRequired(job);
        if (active.ExecutionState != JobExecutionState.Running) throw new CpfException("CPF1241", "Record unit requires an active job.");
    }
    private void Remove(JobKey job, RecordUnit unit)
    {
        unit.Dispose();
        using var connection = factory.Locks.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM record_transactions WHERE job_number=$number AND owner=$owner"; command.Parameters.AddWithValue("$number", job.Number); command.Parameters.AddWithValue("$owner", unit.Id); command.ExecuteNonQuery();
        Live.TryRemove(new KeyValuePair<(string, JobKey), RecordUnit>((Database, job), unit)); _owned.TryRemove(new KeyValuePair<JobKey, RecordUnit>(job, unit));
    }
    public void Dispose()
    {
        KeyValuePair<JobKey, RecordUnit>[] owned;
        lock (_lifecycle) { if (_disposed) return; _disposed = true; owned = _owned.ToArray(); }
        List<Exception>? errors = null;
        foreach (var entry in owned)
            try { lock (entry.Value.Gate) Remove(entry.Key, entry.Value); }
            catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException("Could not release all record units.", errors);
    }
    internal static void BindJob(SqliteCommand command, JobKey job)
    { command.Parameters.AddWithValue("$number", job.Number); command.Parameters.AddWithValue("$name", job.Name); command.Parameters.AddWithValue("$user", job.User); }
    internal static CpfException Invalid(string text) => new("IPC0211", text);
}

public sealed record RecordUnitInfo(string Definition, string Cycle, long PendingChanges, IReadOnlyList<QualifiedName> Files);

/// <summary>One synchronous statement scope inside the job's unit. Dispose without Complete
/// backs out this scope. Execute, complete and dispose on the opening thread, without awaiting.</summary>
public sealed class RecordOperation : IDisposable
{
    private readonly JobRecordTransactions.RecordUnit _unit;
    private bool _complete, _disposed, _failed;
    internal RecordOperation(JobRecordTransactions.RecordUnit unit, SqliteTransaction transaction)
    { _unit = unit; Transaction = transaction; }
    internal SqliteConnection Connection => _unit.Data;
    internal SqliteTransaction Transaction { get; }
    public int Execute(string sql, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        ObjectDisposedException.ThrowIf(_disposed || _complete, this);
        RequireSuccessfulScope();
        using var command = Connection.CreateCommand(); command.Transaction = Transaction; command.CommandText = sql;
        if (parameters is not null) foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        return Guard(() => SqliteCancellation.Run(Connection, _unit.CancellationToken, () => {
            _unit.PrepareRecords(command, Transaction); return command.ExecuteNonQuery();
        }));
    }
    public IReadOnlyList<object?[]> Query(string sql, IReadOnlyDictionary<string, object?>? parameters = null, int maximum = 1000)
    {
        if (maximum is < 1 or > 10000) throw JobRecordTransactions.Invalid("Query limit requires 1–10000 rows.");
        ObjectDisposedException.ThrowIf(_disposed || _complete, this);
        RequireSuccessfulScope();
        using var command = Connection.CreateCommand(); command.Transaction = Transaction; command.CommandText = sql;
        if (parameters is not null) foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        return Guard(() => SqliteCancellation.Run<IReadOnlyList<object?[]>>(Connection, _unit.CancellationToken, () => {
            _unit.PrepareRecords(command, Transaction);
            using var reader = command.ExecuteReader(); var rows = new List<object?[]>(); long bytes = 0;
            while (reader.Read())
            {
                if (rows.Count == maximum) throw JobRecordTransactions.Invalid("Narrow the query or use a record cursor.");
                var row = Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray();
                bytes += row.Sum(value => value switch { byte[] data => (long)data.Length, string text => global::System.Text.Encoding.UTF8.GetByteCount(text), _ => 16L });
                if (bytes > 67108864) throw JobRecordTransactions.Invalid("Record query exceeds the 64 MiB result limit."); rows.Add(row);
            }
            return rows;
        }));
    }
    internal void Prepare(SqliteCommand command)
    {
        ObjectDisposedException.ThrowIf(_disposed || _complete, this); RequireSuccessfulScope();
        Guard(() => SqliteCancellation.Run(Connection, _unit.CancellationToken, () => { _unit.PrepareRecords(command, Transaction); return 0; }));
    }
    private T Guard<T>(Func<T> operation) { try { return operation(); } catch { _failed = true; throw; } }
    private void RequireSuccessfulScope() { if (_failed) throw JobRecordTransactions.Invalid("The record statement failed; dispose this scope before retrying."); }
    public void Complete() { ObjectDisposedException.ThrowIf(_disposed, this); RequireSuccessfulScope(); _unit.CancellationToken.ThrowIfCancellationRequested(); _complete = true; }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { _unit.FinishOperation(Transaction, _complete); }
        finally { try { Transaction.Dispose(); } finally { Monitor.Exit(_unit.Gate); } }
    }
}
