using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

/// <summary>One host's live job handles. Durable controls are polled without inheriting a caller identity.</summary>
public sealed class JobRuntimeManager(SqliteConnectionFactory factory) : IDisposable
{
    private readonly ConcurrentDictionary<int, JobRuntimeLease> _jobs = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;
    public string? Failure { get; private set; }

    internal JobRuntimeLease Attach(Job job, CancellationToken parent)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Failure is not null) throw new CpfException("IPC0123", Failure);
            var lease = new JobRuntimeLease(factory, job, parent, () => _jobs.TryRemove(job.Key.Number, out _));
            if (!_jobs.TryAdd(job.Key.Number, lease)) { lease.DisposeUnregistered(); throw new CpfException("IPC0123", "Job already has an execution owner."); }
            if (_timer is null)
            {
                using var suppressed = ExecutionContext.SuppressFlow();
                _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
            }
            return lease;
        }
    }

    public JobCallEnvironment? Environment(JobKey job)
    {
        new JobDataAreaStore(factory).RequireOwner(job);
        return _jobs.TryGetValue(job.Number, out var lease) ? lease.Environment : null;
    }

    internal JobActivationGroups? Groups(JobKey job) => _jobs.TryGetValue(job.Number, out var lease) ? lease.ActivationGroups : null;

    internal IDisposable? TrackProcess(JobKey? job, Process process) => job is { } key && _jobs.TryGetValue(key.Number, out var lease)
        ? lease.TrackProcess(process) : null;

    private void Poll()
    {
        lock (_gate)
        {
            if (_disposed || _jobs.IsEmpty) return;
            try
            {
                using var connection = factory.Open(); using var command = connection.CreateCommand();
                command.CommandText = "SELECT number,cancel_reason FROM sys_jobs WHERE number IN (" + string.Join(',', _jobs.Keys) + ") AND (cancel_requested=1 OR execution_state NOT IN ('Queued','Running'))";
                using var reader = command.ExecuteReader();
                while (reader.Read()) if (_jobs.TryGetValue(reader.GetInt32(0), out var lease))
                    lease.Cancel(reader.IsDBNull(1) ? "Job ended." : reader.GetString(1));
                reader.Close();
                foreach (var lease in _jobs.Values) lease.Sample();
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException or ObjectDisposedException)
            {
                Failure = "Job controls unavailable (" + ex.GetType().Name + ").";
                foreach (var lease in _jobs.Values) lease.Cancel(Failure);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return; _disposed = true; _timer?.Dispose();
            foreach (var lease in _jobs.Values) lease.Cancel("Host disposed.");
        }
    }
}

internal sealed class JobRuntimeLease : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Job _job;
    private readonly CancellationTokenSource _stop;
    private readonly Action _detach;
    private bool _disposed;
    private int _executing;
    private AccountingScope? _accounting;
    private readonly ConcurrentDictionary<int, NativeProcessAccounting> _processes = new();
    public CancellationToken CancellationToken => _stop.Token;
    public string? CancellationReason { get; private set; }
    internal JobActivationGroups ActivationGroups { get; }
    public JobCallEnvironment Environment { get; }

    internal JobRuntimeLease(SqliteConnectionFactory factory, Job job, CancellationToken parent, Action detach)
    { _factory = factory; _job = job; _stop = CancellationTokenSource.CreateLinkedTokenSource(parent); _detach = detach; ActivationGroups = new JobActivationGroups(factory, job.Key); Environment = new JobCallEnvironment(factory, job.Key); }
    internal void Cancel(string reason)
    {
        CancellationReason = reason;
        try { _stop.Cancel(); } catch (ObjectDisposedException) { }
    }
    internal IDisposable EnterCommand()
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _executing, 1) != 0) throw new CpfException("IPC0123", "A job may execute one command at a time.");
        try
        {
            var command = new AccountingScope(this);
            command.Start(); Volatile.Write(ref _accounting, command); return command;
        }
        catch { Volatile.Write(ref _executing, 0); throw; }
    }
    internal void Sample()
    {
        Volatile.Read(ref _accounting)?.Sample();
        foreach (var process in _processes.Values) process.Sample();
    }
    internal IDisposable TrackProcess(Process process)
    {
        var pid = process.Id;
        var accounting = new NativeProcessAccounting(_factory, _job.Key, process, () => _processes.TryRemove(pid, out _));
        _processes[pid] = accounting; return accounting;
    }
    internal void DisposeUnregistered() { _disposed = true; _stop.Dispose(); }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _detach();
        try { Environment.Dispose(); } finally { try { ActivationGroups.Dispose(); } finally { _stop.Dispose(); } }
    }

    private sealed class AccountingScope(JobRuntimeLease lease) : IDisposable
    {
        private readonly int _managed = System.Environment.CurrentManagedThreadId;
        private readonly int _native = OperatingSystem.IsLinux() ? GetTid() : 0;
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly int _clock = ThreadClock();
        private long _lastCpu;
        private long _lastElapsed;
        private readonly object _sampleGate = new();
        private bool _ended;
        internal void Start() { _lastCpu = Cpu(_clock); Write(false, true); }
        internal void Sample() { lock (_sampleGate) { if (!_ended) Write(false, false); } }
        private void Write(bool end, bool start)
        {
            var cpuNow = Cpu(_clock); var elapsedNow = (long)Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            var cpu = start ? 0 : Math.Max(0, cpuNow - _lastCpu);
            var elapsed = start ? 0 : Math.Max(0, elapsedNow - _lastElapsed);
            using var connection = lease._factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sys_job_threads(job_number,managed_thread,native_thread,state,cpu_nanoseconds,elapsed_milliseconds,updated_at)
                VALUES($job,$managed,$native,$state,$cpu,$elapsed,$at)
                ON CONFLICT(job_number,managed_thread) DO UPDATE SET native_thread=$native,state=$state,
                  cpu_nanoseconds=cpu_nanoseconds+$cpu,elapsed_milliseconds=elapsed_milliseconds+$elapsed,updated_at=$at;
                UPDATE sys_job_accounting SET cpu_nanoseconds=cpu_nanoseconds+$cpu,elapsed_milliseconds=elapsed_milliseconds+$elapsed,
                  active_threads=$active,commands=commands+$commands WHERE job_number=$job;
                """;
            command.Parameters.AddWithValue("$job", lease._job.Key.Number); command.Parameters.AddWithValue("$managed", _managed);
            command.Parameters.AddWithValue("$native", _native); command.Parameters.AddWithValue("$state", end ? "Idle" : "Executing");
            command.Parameters.AddWithValue("$cpu", cpu); command.Parameters.AddWithValue("$elapsed", elapsed);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.Parameters.AddWithValue("$active", end ? 0 : 1);
            command.Parameters.AddWithValue("$commands", start ? 1 : 0); command.ExecuteNonQuery(); transaction.Commit();
            _lastCpu = cpuNow; _lastElapsed = elapsedNow;
        }
        public void Dispose()
        {
            lock (_sampleGate)
            {
                if (_ended) return; _ended = true;
                try { Write(true, false); }
                finally { Volatile.Write(ref lease._accounting, null); Volatile.Write(ref lease._executing, 0); }
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Timespec { public long Seconds; public long Nanoseconds; }
    [DllImport("libc", EntryPoint = "clock_gettime", SetLastError = true)] private static extern int ClockGetTime(int clock, out Timespec result);
    [DllImport("libc", EntryPoint = "gettid")] private static extern int GetTid();
    [DllImport("libc", EntryPoint = "pthread_self")] private static extern nuint PthreadSelf();
    [DllImport("libc", EntryPoint = "pthread_getcpuclockid")] private static extern int PthreadGetCpuClock(nuint thread, out int clock);
    private static int ThreadClock() => OperatingSystem.IsLinux() && PthreadGetCpuClock(PthreadSelf(), out var clock) == 0 ? clock : int.MinValue;
    private static long Cpu(int clock) => clock != int.MinValue && ClockGetTime(clock, out var value) == 0 ? value.Seconds * 1000000000 + value.Nanoseconds : 0;
}
