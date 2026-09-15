using System.Diagnostics;

namespace Ipc.Core.Work;

/// <summary>Cooperative scheduling at interpreter boundaries; it does not change a pooled OS thread's priority.</summary>
public static class JobExecutionBudget
{
    private static readonly AsyncLocal<State?> Current = new();
    public static IDisposable Enter(Job job)
    {
        var previous = Current.Value;
        Current.Value = new State(Math.Clamp(job.TimeSliceMilliseconds, 1, 10000), Math.Clamp(job.RunPriority, 1, 99));
        return new Scope(previous);
    }

    public static void Checkpoint()
    {
        var state = Current.Value;
        if (state is null || (++state.Instructions & 255) != 0 || Stopwatch.GetElapsedTime(state.SliceStart).TotalMilliseconds < state.TimeSlice) return;
        // Lower classes yield longer at a quantum boundary. Native process scheduling
        // is handled separately; this never changes priority for unrelated thread-pool work.
        Thread.Sleep(state.RunPriority / 20);
        state.SliceStart = Stopwatch.GetTimestamp();
    }
    private sealed class State(int slice, int priority)
    { public int TimeSlice { get; } = slice; public int RunPriority { get; } = priority; public long SliceStart = Stopwatch.GetTimestamp(); public long Instructions; }
    private sealed class Scope(State? previous) : IDisposable
    { public void Dispose() => Current.Value = previous; }
}
