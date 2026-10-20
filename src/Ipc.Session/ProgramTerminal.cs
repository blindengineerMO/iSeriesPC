using System.Runtime.ExceptionServices;
using Ipc.Cl.Commands;
using Ipc.Core.Objects;
using Ipc.Dsp;

namespace Ipc.Session;

public interface IProgramTerminal
{
    PanelResponse Read(ProgramDisplay display, CancellationToken cancellationToken);
}

/// <summary>A suspended program's display. Access checks run with its captured call authority.</summary>
public sealed class ProgramDisplay : IDisposable
{
    private readonly DisplayFileSession? _session;
    private readonly Ipc.Terminal.DisplayBuffer? _buffer;
    private readonly Func<Ipc.Terminal.KeyPress, PanelResponse>? _handle;
    private readonly Action _validate;
    private ExecutionContext? _context = ExecutionContext.Capture();
    private bool _ended;
    public ProgramDisplay(DisplayFileSession session, QualifiedName file, Action validate)
    { _session = session; File = file; _validate = validate; }
    public ProgramDisplay(Ipc.Terminal.DisplayBuffer buffer, Func<Ipc.Terminal.KeyPress, PanelResponse> handle,
        QualifiedName resource, Action validate, string state)
    { _buffer = buffer; _handle = handle; File = resource; _validate = validate; State = state; }
    public DisplayFileSession Session => _session ?? throw new InvalidOperationException("This display is not a DDS file.");
    public Ipc.Terminal.DisplayBuffer Buffer => _buffer ?? Session.Buffer;
    public string State { get; } = "ProgramDisplay";
    public PanelResponse Handle(Ipc.Terminal.KeyPress key) => _handle?.Invoke(key) ?? Session.Handle(key);
    public QualifiedName File { get; }
    public void CheckAccess() { InContext(() => { _validate(); return 0; }); }
    internal T InContext<T>(Func<T> action)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended), this);
        var context = _context;
        if (context is null) return action();
        T result = default!;
        ExecutionContext.Run(context.CreateCopy(), _ => { ObjectDisposedException.ThrowIf(Volatile.Read(ref _ended), this); result = action(); }, null); return result;
    }
    public void Dispose() { Volatile.Write(ref _ended, true); _context = null; }
}

/// <summary>Executes one command on its own worker; each display wait hands control to the terminal.</summary>
internal sealed class ProgramTerminal(CancellationToken cancellationToken) : IProgramTerminal, IDisposable
{
    private sealed record Pending(ProgramDisplay Display, TaskCompletionSource<PanelResponse> Answer);
    private readonly object _gate = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    private Task? _worker;
    private Pending? _pending;
    private CommandResult? _result;
    private ExceptionDispatchInfo? _error;
    private bool _finished;
    private bool _disposed;
    public bool IsRunning { get { lock (_gate) return _worker is not null && !_finished; } }
    public ProgramDisplay? Display { get { lock (_gate) return _pending?.Display; } }

    public CommandResult? Start(Func<CommandResult> execute)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Task? previous;
        lock (_gate)
        {
            if (_worker is not null && !_finished) throw new InvalidOperationException("A terminal program is already running.");
            previous = _worker;
        }
        previous?.GetAwaiter().GetResult();
        lock (_gate)
        {
            if (_worker is not null && !_finished) throw new InvalidOperationException("A terminal program is already running.");
            _pending = null; _result = null; _error = null; _finished = false;
            _worker = Task.Factory.StartNew(() =>
            {
                try { var result = execute(); lock (_gate) _result = result; }
                catch (Exception error) { lock (_gate) _error = ExceptionDispatchInfo.Capture(error); }
                finally { lock (_gate) { _finished = true; _pending = null; } _changed.Set(); }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        return WaitForYield();
    }

    public PanelResponse Read(ProgramDisplay display, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var pending = new Pending(display, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            if (_worker is null || _finished || _pending is not null) throw new InvalidOperationException("Display input requires an active terminal command.");
            _pending = pending;
        }
        _changed.Set();
        return pending.Answer.Task.WaitAsync(linked.Token).GetAwaiter().GetResult();
    }

    public CommandResult? Resume(PanelResponse response) => Answer(pending => pending.Answer.TrySetResult(response));
    public CommandResult? Fail(Exception error) => Answer(pending => pending.Answer.TrySetException(error));
    private CommandResult? Answer(Action<Pending> answer)
    {
        Pending pending;
        lock (_gate) { pending = _pending ?? throw new InvalidOperationException("No program is waiting for terminal input."); _pending = null; }
        answer(pending); return WaitForYield();
    }

    private CommandResult? WaitForYield()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_finished) { _error?.Throw(); return _result ?? throw new InvalidOperationException("Terminal command returned no result."); }
                if (_pending is not null) return null;
            }
            WaitHandle.WaitAny([_changed, _stop.Token.WaitHandle]);
            _stop.Token.ThrowIfCancellationRequested();
        }
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _stop.Cancel();
        // The owner cancels the job token before disposing an executing worker.
        // Join before releasing call frames, allocations or the job runtime.
        _worker?.GetAwaiter().GetResult();
        _changed.Dispose(); _stop.Dispose();
    }
}
