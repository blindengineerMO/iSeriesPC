using System.Diagnostics;
using System.Runtime.InteropServices;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed class TtySession
{
    private readonly ISessionController _initialController;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly AnsiRenderer _renderer = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _terminalState;

    public TtySession(IpcSystem system, TextReader input, TextWriter output)
    {
        _initialController = new SignOnController(system, cancellationToken: _stop.Token, unixAccount: Ipc.Session.Transport.UnixPeerIdentity.CurrentAccount());
        _input = input;
        _output = output;
    }

    public TtySession(ISessionController controller, TextReader input, TextWriter output)
    {
        _initialController = controller;
        _input = input;
        _output = output;
    }

    public int ExitCode { get; private set; }

    public ISessionController? Controller { get; private set; }

    public void Run()
    {
        ISessionController? controller = _initialController;
        Controller = controller;
        var parser = new TerminalParser();
        DisplayBuffer? previous = null;
        var size = TerminalSize.Read();
        var stopCode = 0;
        var ended = false;
        Task<int>? pendingRead = null;
        var signals = new List<PosixSignalRegistration>();
        void Stop(int code)
        {
            Interlocked.CompareExchange(ref stopCode, code, 0);
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            if (_input is TerminalInputReader terminalInput) terminalInput.Stop();
            Volatile.Read(ref controller)?.RequestDisconnect();
        }
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var (signal, code) in new[] { (PosixSignal.SIGHUP, 129), (PosixSignal.SIGINT, 130),
                    (PosixSignal.SIGQUIT, 131), (PosixSignal.SIGTERM, 143), (PosixSignal.SIGTSTP, 148) })
                    signals.Add(PosixSignalRegistration.Create(signal, context => { context.Cancel = true; Stop(code); }));
            }
            EnterRawMode();
            _output.Write("\u001b[?7l\u001b[?2004h"); // Prevent bottom-right autowrap; request bracketed paste.
            RenderCurrent(force: true);
            var lastInput = Stopwatch.GetTimestamp();
            while (controller is not null && Volatile.Read(ref stopCode) == 0)
            {
                var latestSize = TerminalSize.Read();
                if (size != latestSize) { size = latestSize; RenderCurrent(force: true); }
                pendingRead ??= Task.Run(_input.Read);
                IReadOnlyList<KeyPress> keys;
                if (!pendingRead.Wait(50))
                {
                    if (!parser.HasPendingEscape || Stopwatch.GetElapsedTime(lastInput) < TimeSpan.FromMilliseconds(250)) continue;
                    keys = parser.FlushPending();
                }
                else
                {
                    var next = pendingRead.GetAwaiter().GetResult();
                    if (next < 0) { Stop(1); break; }
                    if (!parser.IsPasting && !parser.HasPendingEscape && next is 3 or 4)
                    { Stop(next == 3 ? 130 : 1); break; }
                    lastInput = Stopwatch.GetTimestamp(); keys = parser.Feed((char)next);
                    pendingRead = null;
                }
                if (keys.Count == 0) continue;
                foreach (var keyPress in keys)
                {
                    if (!Fits() && keyPress.Aid is not (AidKey.Pf3 or AidKey.Pa1)) continue;
                    var evt = controller.Handle(keyPress);
                    if (evt.Next is { } nextController)
                    {
                        var previousController = controller; controller = nextController; Controller = controller; previous = null;
                        if (!ReferenceEquals(previousController, nextController) && previousController is IDisposable previousDisposable) previousDisposable.Dispose();
                        break;
                    }
                    if (evt.EndSession)
                    {
                        ExitCode = evt.State == "Failed" ? 1 : 0; RenderCurrent(force: false);
                        if (evt.State == "Failed") Stop(1); else ended = true;
                        return;
                    }
                }
                RenderCurrent(force: false);
            }
        }
        catch (Exception error) when (Volatile.Read(ref stopCode) != 0 && error is IOException or ObjectDisposedException or OperationCanceledException)
        { /* A requested disconnect closes a pending socket/read or cancels command execution. */ }
        finally
        {
            if (!ended) { Stop(Volatile.Read(ref stopCode) is 0 ? 1 : stopCode); ExitCode = stopCode; }
            if (_input is TerminalInputReader terminalInput) terminalInput.Stop();
            // The owned native reader leaves poll within 50 ms, including a partial UTF-8 character.
            if (_input is TerminalInputReader && pendingRead is not null)
                try { pendingRead.GetAwaiter().GetResult(); } catch (IOException) { }
            try
            {
                _output.Write("\u001b[0m\u001b[?2004l\u001b[?7h"); _output.Flush();
            }
            catch (IOException) when (Volatile.Read(ref stopCode) != 0) { }
            finally
            {
                try { RestoreTerminal(); }
                finally
                {
                    try { if (controller is IDisposable disposable) disposable.Dispose(); }
                    finally { foreach (var signal in signals) signal.Dispose(); _stop.Dispose(); }
                }
            }
        }
        bool Fits() => size is null || size.Value.Rows >= controller!.Buffer.Rows && size.Value.Columns >= controller.Buffer.Columns;
        void RenderCurrent(bool force)
        {
            var buffer = controller!.Buffer;
            if (!Fits())
            {
                var warning = new DisplayBuffer(size!.Value.Rows, size.Value.Columns);
                var message = $"Resize terminal to {buffer.Columns}x{buffer.Rows}. Input paused. F3 returns.";
                for (var i = 0; i < Math.Min(message.Length, warning.Rows * warning.Columns); i++)
                    warning.Set(i / warning.Columns + 1, i % warning.Columns + 1, message[i], DisplayAttribute.HighIntensity);
                buffer = warning;
            }
            if (force || previous is null) { _output.Write(_renderer.Render(buffer)); _output.Flush(); }
            else WriteDiff(previous, buffer);
            previous = buffer.Clone();
        }
    }

    private void WriteDiff(DisplayBuffer previous, DisplayBuffer current)
    {
        var diff = _renderer.RenderDiff(current, previous, out var changed);
        if (changed)
        {
            _output.Write(diff);
            _output.Flush();
        }
    }

    private void EnterRawMode()
    {
        if (!OperatingSystem.IsLinux() || global::System.Console.IsInputRedirected) return;
        _terminalState = RunTerminalCommand("-g").Trim();
        try { RunTerminalCommand("raw", "-echo"); }
        catch { RestoreTerminal(); throw; }
    }

    private void RestoreTerminal()
    {
        if (_terminalState is not { } state) return;
        _terminalState = null;
        RunTerminalCommand(state);
    }

    private static string RunTerminalCommand(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("stty")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-F");
        startInfo.ArgumentList.Add("/dev/tty");
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new IOException("Cannot start terminal configuration.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"Terminal configuration failed: {error.Trim()}");
        return output;
    }
}
