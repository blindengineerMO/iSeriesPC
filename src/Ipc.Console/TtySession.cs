using System.Diagnostics;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Console.Session;

public sealed class TtySession
{
    private readonly IpcSystem _system;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly AnsiRenderer _renderer = new();

    public TtySession(IpcSystem system, TextReader input, TextWriter output)
    {
        _system = system;
        _input = input;
        _output = output;
    }

    public int ExitCode { get; private set; }

    public ISessionController? Controller { get; private set; }

    public void Run()
    {
        ISessionController? controller = new SignOnController(_system);
        Controller = controller;
        var parser = new TerminalParser();
        var previous = controller.Buffer.Clone();

        EnterRawMode();
        try
        {
            _output.Write(_renderer.Render(controller.Buffer));
            _output.Flush();

            while (controller is not null)
            {
                var next = _input.Read();
                if (next < 0)
                {
                    break;
                }

                foreach (var keyPress in parser.Feed((char)next))
                {
                    var evt = controller.Handle(keyPress);
                    if (evt.Next is { } nextController)
                    {
                        controller = nextController;
                        Controller = controller;
                        break;
                    }

                    if (evt.EndSession)
                    {
                        ExitCode = evt.State == "Failed" ? 1 : 0;
                        WriteDiff(previous, controller.Buffer);
                        previous = controller.Buffer.Clone();
                        return;
                    }
                }

                WriteDiff(previous, controller.Buffer);
                previous = controller.Buffer.Clone();
            }
        }
        finally
        {
            RestoreTerminal();
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
        try
        {
            RunTerminalCommand("-raw -echo");
        }
        catch
        {
        }
    }

    private void RestoreTerminal()
    {
        try
        {
            RunTerminalCommand("-sane");
        }
        catch
        {
        }
    }

    private static void RunTerminalCommand(string arguments)
    {
        var startInfo = new ProcessStartInfo("stty", arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(startInfo);
        if (process is not null)
        {
            process.WaitForExit();
        }
    }
}