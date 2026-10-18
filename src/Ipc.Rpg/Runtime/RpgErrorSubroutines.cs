using Ipc.Core.Messages;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private sealed class ExecutionFrame(IReadOnlyList<RpgStatement> statements, RpgBlockState state, object invocation)
    {
        internal IReadOnlyList<RpgStatement> Statements { get; } = statements;
        internal RpgBlockState State { get; } = state;
        internal object Invocation { get; } = invocation;
        internal int Pc { get; set; }
    }
    private sealed class ErrorRoutineBranch(ExecutionFrame target, int index) : Exception
    { internal ExecutionFrame Target { get; } = target; internal int Index { get; } = index; }
    private readonly Stack<ExecutionFrame> _executionFrames = new();
    private readonly Stack<RpgSubroutine> _errorSubroutineCalls = new();
    private string? _procedure;
    private object _procedureInvocation = new();

    private bool HasEnclosingMonitor(RpgRuntimeException error) => error.StatusCode is { } status &&
        _executionFrames.Skip(1).TakeWhile(f => ReferenceEquals(f.Invocation, _procedureInvocation))
            .Any(f => MonitorHandler(f.Statements, f.Pc, status, f.State, unwind: false) >= 0);

    private bool InvokeErrorSubroutine(RpgRuntimeException error, RpgRuntimeContext context)
    {
        if (error.SuppressErrorSubroutines || !error.ErrorHandlerInvocations.Add(_procedureInvocation)) return false;
        var name = error.FileError ? context.Program.FileErrorHandlers.SingleOrDefault(f => f.File == error.StatusFile)?.Routine : "*PSSR";
        var routine = name is null ? null : context.Program.FindSubroutine(name, _procedure);
        if (routine is null) return false;
        ExecuteSubroutine(routine, context, automatic: true);
        return _programEnded || _returned || context.Indicators[0];
    }
    private void ExecuteSubroutine(RpgSubroutine routine, RpgRuntimeContext context, bool automatic = false)
    {
        var isError = routine.Name == "*PSSR" || routine.Procedure is null && context.Program.FileErrorHandlers.Any(f => f.Routine == routine.Name);
        if (isError && _errorSubroutineCalls.Count >= 64)
            throw new RpgRuntimeException("RPG error-subroutine nesting exceeds 64 invocations.") { StatusCode = 9999, SuppressErrorSubroutines = true };
        if (isError) _errorSubroutineCalls.Push(routine);
        try
        {
            Execute(routine.Statements, context, new RpgBlockState(), routine.Name);
            if (automatic && (routine.Procedure is not null || context.Program.IsNativeProcedure) && !_returned && !_programEnded && !context.Indicators[0])
                throw new CpfException("RNX9001", "RPG subprocedure failed after *PSSR: " + (routine.Procedure ?? context.Program.NativeProcedureName) + ".");
            if (!isError || _programEnded || _returned || context.Indicators[0] || routine.Ending?.Factor2 is not { Length: > 0 } point) return;
            var expression = RpgExpressionParser.Parse(point);
            var value = RpgValues.ToText(expression.Eval(context)).TrimEnd();
            if (expression is not RpgLiteral && (expression is not RpgFieldRef field || context.Program.FindField(field.Name)?.Source != RpgFieldSource.Constant))
                WriteTarget(point, string.Empty, context);
            if (value.Length == 0) return;
            if (value != "*CANCL")
            {
                var error = routine.Ending.Location is { } location ? new RpgRuntimeException(location, "Unsupported RPG cycle return point: " + value)
                    { StatusCode = 9999, SuppressErrorSubroutines = true } : new RpgRuntimeException("Unsupported RPG cycle return point: " + value)
                    { StatusCode = 9999, SuppressErrorSubroutines = true };
                context.SetFeedbackOperation(routine.Ending, routine.Name); context.RecordStatus(9999); context.RecordFeedbackError(error); throw error;
            }
            context.IsCancelled = true; _programEnded = true;
            throw new CpfException("RNX9001", "RPG program cancelled by error subroutine " + routine.Name + ".");
        }
        finally { if (isError) _errorSubroutineCalls.Pop(); }
    }
    private void BranchFromErrorRoutine(string? label, IReadOnlyList<RpgStatement> statements)
    {
        if (!_errorSubroutineCalls.TryPeek(out var routine) || !ReferenceEquals(routine.Statements, statements)) return;
        var body = _executionFrames.Where(f => ReferenceEquals(f.Invocation, _procedureInvocation)).Last();
        for (var index = 0; index < body.Statements.Count; index++)
            if (body.Statements[index].Opcode == RpgOpcode.Tag && string.Equals(body.Statements[index].Factor1, label, StringComparison.OrdinalIgnoreCase))
                throw new ErrorRoutineBranch(body, index);
    }
}
