using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class InterpreterCancellationTests
{
    [Fact]
    public async Task Cl_loop_observes_cancellation_after_execution_starts()
    {
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var program = new ClCompiler().Compile("LOOP", "QGPL",
            "PGM\nLOOP:\nSNDPGMMSG MSG('tick')\nGOTO LOOP\nENDPGM");
        var interpreter = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(),
            _ => started.Set(), stop.Token);
        var run = Task.Factory.StartNew(() => interpreter.Run(program), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "CL loop did not reach its first statement."); }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [Fact]
    public async Task Rpg_loop_observes_cancellation_without_swallowing_it_in_error_handler()
    {
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        var handled = false;
        var program = RpgCompiler.Compile("LOOP", "QGPL", "**free\ndow 1 = 1;\ndsply 'tick';\nenddo;\non-error;\ndsply 'handler';");
        var interpreter = new RpgInterpreter(new RpgHost
        {
            CancellationToken = stop.Token,
            Display = text => { if (text == "tick") started.Set(); else handled = true; },
        });
        var run = Task.Factory.StartNew(() => interpreter.Run(program), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "RPG loop did not reach its first statement."); }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.False(handled);
    }

    [Fact]
    public void Cl_sibling_calls_do_not_accumulate_recursion_depth()
    {
        var compiler = new ClCompiler();
        var child = compiler.Compile("CHILD", "QGPL", "PGM\nENDPGM");
        var parent = compiler.Compile("PARENT", "QGPL",
            "PGM\n" + string.Concat(Enumerable.Repeat("CALL PGM(CHILD)\n", 30)) + "ENDPGM");
        var interpreter = new ClInterpreter((_, _) => child, _ => CommandResult.Ok());
        Assert.False(interpreter.Run(parent).IsError);
    }
}
