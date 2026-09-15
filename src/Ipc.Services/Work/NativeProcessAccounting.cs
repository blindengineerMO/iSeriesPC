using System.Diagnostics;
using Ipc.Core.Work;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

internal sealed class NativeProcessAccounting : IDisposable
{
    private readonly SqliteConnectionFactory _factory;
    private readonly Process _process;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly Action _detach;
    private long _cpu;
    private int _peakThreads;
    private bool _ended;
    internal NativeProcessAccounting(SqliteConnectionFactory factory, JobKey job, Process process, Action detach)
    {
        _factory = factory; _process = process; _detach = detach;
        using var connection = factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO sys_job_processes(invocation,job_number,process_id,state,started_at) VALUES($id,$job,$pid,'Running',$at)";
        command.Parameters.AddWithValue("$id", _id); command.Parameters.AddWithValue("$job", job.Number);
        command.Parameters.AddWithValue("$pid", process.Id); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
        Sample();
    }
    internal void Sample()
    {
        lock (_gate) { if (!_ended) Write(false); }
    }
    private void Write(bool end)
    {
        try
        {
            _process.Refresh();
            _cpu = Math.Max(_cpu, _process.TotalProcessorTime.Ticks * 100);
            if (!_process.HasExited) _peakThreads = Math.Max(_peakThreads, _process.Threads.Count);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        { /* Preserve the most recent sample when the OS has already removed process accounting. */ }
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE sys_job_processes SET cpu_nanoseconds=$cpu,peak_threads=$threads,state=$state,exit_code=$exit,ended_at=$at WHERE invocation=$id";
        command.Parameters.AddWithValue("$id", _id); command.Parameters.AddWithValue("$cpu", _cpu); command.Parameters.AddWithValue("$threads", _peakThreads);
        command.Parameters.AddWithValue("$state", end ? "Exited" : "Running");
        command.Parameters.AddWithValue("$exit", end && _process.HasExited ? _process.ExitCode : DBNull.Value);
        command.Parameters.AddWithValue("$at", end ? DateTimeOffset.UtcNow.ToString("O") : DBNull.Value); command.ExecuteNonQuery();
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_ended) return; _ended = true;
            try { Write(true); } finally { _detach(); }
        }
    }
}
