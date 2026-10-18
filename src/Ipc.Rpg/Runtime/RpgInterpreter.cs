using System.Globalization;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private readonly RpgHost _host;
    private readonly Dictionary<string, RpgFileCursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RpgFileHandle> _fileHandles = new(StringComparer.OrdinalIgnoreCase);
    private bool _programEnded;
    private bool _returned;
    private bool _handlingError;
    private int _depth;

    public RpgInterpreter(RpgHost host) => _host = host;

    public static IReadOnlyList<string> EntryParameterNames(RpgProgram program) => program.MainStatements
        .Where(statement => statement.Opcode == RpgOpcode.Parm && InEntryPlist(program.MainStatements, statement))
        .Select(statement => statement.Factor1 ?? throw new RpgRuntimeException("RPG entry PARM requires a field.")).ToArray();

    public RpgRuntimeContext Run(RpgProgram program, IReadOnlyList<object?>? parameters = null, RpgRuntimeContext? existingContext = null)
    {
        _host.CancellationToken.ThrowIfCancellationRequested();
        var context = existingContext is { IsCancelled: false } ? existingContext : new RpgRuntimeContext(program, _host);
        context.RebindHost(_host);
        _context = context;
        _files.Clear();
        _programEnded = false;
        _returned = false;
        _handlingError = false;
        _depth = 0;
        _procedure = null; _procedureInvocation = new object(); _executionFrames.Clear(); _errorSubroutineCalls.Clear();
        try
        {
            context.BeginFeedbackCall(parameters?.Count ?? 0);
            BindEntryParameters(program, context, parameters);
            InitializeExternalFiles(context);
            Execute(program.MainStatements, context, new RpgBlockState());
            return context;
        }
        catch (RpgRuntimeException error) when (error.ServiceError is Ipc.Core.Messages.CpfException service)
        { context.IsCancelled = true; throw LocatedService(service, error.Location); }
        catch { context.IsCancelled = true; throw; }
        finally
        {
            try { foreach (var handle in _fileHandles.Values) handle.Dispose(); }
            finally
            {
                try { if (!_host.RetainWorkstations || context.Indicators[0] || context.IsCancelled) context.CloseWorkstations(); }
                finally
                {
                    try { if (!_host.RetainFiles || context.Indicators[0] || context.IsCancelled) context.CloseDiskFiles(); }
                    finally { _fileHandles.Clear(); _files.Clear(); _context = null; context.ReleaseReferences(); }
                }
            }
        }
    }

    private void BindEntryParameters(RpgProgram program, RpgRuntimeContext context, IReadOnlyList<object?>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return;
        }

        var index = 0;
        if (parameters.Any(p => p is Ipc.Core.Work.ProgramArgument) &&
            parameters.Count != program.MainStatements.Count(s => s.Opcode == RpgOpcode.Parm && InEntryPlist(program.MainStatements, s)))
            throw new RpgRuntimeException("RPG entry parameter count does not match the caller.");
        foreach (var statement in program.MainStatements)
        {
            if (statement.Opcode == RpgOpcode.Plist && string.Equals(statement.Factor1, "*ENTRY", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (statement.Opcode == RpgOpcode.Parm && InEntryPlist(program.MainStatements, statement))
            {
                if (index < parameters.Count && statement.Factor1 is not null)
                {
                    if (parameters[index] is Ipc.Core.Work.ProgramArgument reference) context.BindReference(statement.Factor1, reference);
                    else context.WriteValue(statement.Factor1, parameters[index]);
                }

                index++;
            }
        }
    }

    private static bool InEntryPlist(IReadOnlyList<RpgStatement> statements, RpgStatement parm)
    {
        var index = -1;
        for (var i = 0; i < statements.Count; i++)
        {
            if (ReferenceEquals(statements[i], parm))
            {
                index = i;
                break;
            }
        }

        if (index <= 0)
        {
            return false;
        }

        for (var back = index - 1; back >= 0; back--)
        {
            var stmt = statements[back];
            switch (stmt.Opcode)
            {
                case RpgOpcode.Plist:
                    return string.Equals(stmt.Factor1, "*ENTRY", StringComparison.OrdinalIgnoreCase);
                case RpgOpcode.Parm:
                    continue;
                default:
                    return false;
            }
        }

        return false;
    }

    private static bool ConditionsMet(RpgStatement statement, RpgRuntimeContext context)
    {
        foreach (var condition in statement.Conditions)
        {
            var value = context.Indicators[condition.Indicator];
            if (condition.Negated)
            {
                value = !value;
            }

            if (!value)
            {
                return false;
            }
        }

        return true;
    }

    private void Execute(IReadOnlyList<RpgStatement> statements, RpgRuntimeContext context, RpgBlockState state, string routine = "*DETC")
    {
        var frame = new ExecutionFrame(statements, state, _procedureInvocation); _executionFrames.Push(frame);
        var pc = 0;
        try
        {
            while (pc < statements.Count && !_programEnded && !_returned && !context.Indicators[0])
            {
                frame.Pc = pc;
                try
                {
                    _host.CancellationToken.ThrowIfCancellationRequested();
                    Ipc.Core.Work.JobExecutionBudget.Checkpoint();
                    var statement = statements[pc];
                    if (!ConditionsMet(statement, context))
                    {
                        pc = statement.Opcode is RpgOpcode.Monitor or RpgOpcode.Select ? statement.End + 1 : pc + 1;
                        continue;
                    }

                    if (statement.Opcode == RpgOpcode.OnError)
                    {
                        if (statement.End >= 0) { pc = statement.End + 1; continue; }
                        if (_handlingError)
                        {
                            pc++;
                            continue;
                        }

                        pc = statements.Count;
                        continue;
                    }

                    var jump = 0;
                    context.SetFeedbackOperation(statement, routine);
                    if (statement.ErrorExtender) context.BeginErrorOperation(StatusFile(statement));
                    if (IsFileOperation(statement.Opcode) && DeclaredFile(statement) && statement.Indicator2 != 0) context.Indicators[statement.Indicator2] = false;
                    try
                    {
                        jump = ExecuteOp(statement, statements, context, state);
                    }
                    catch (RpgRuntimeException error)
                    {
                        if (statement.Opcode is RpgOpcode.Call or RpgOpcode.CallB or RpgOpcode.CallP && (error.StatusCode != 202 || error.FeedbackRecorded))
                            error = new(error.Message) { StatusCode = 202, ServiceError = error.ServiceError, StatusFile = error.StatusFile };
                        if (!error.FeedbackRecorded) context.SetFeedbackOperation(statement, routine, preserveCallTarget: true);
                        if (HandleOperationError(error, statement, context)) { pc++; continue; }
                        if (error.StatusCode is { } status && MonitorHandler(statements, pc, status, state) is var monitored && monitored >= 0)
                        { pc = monitored; continue; }
                        if (HasEnclosingMonitor(error)) throw Located(error, statement);
                        if (InvokeErrorSubroutine(error, context)) { pc++; continue; }
                        if (_handlingError)
                        {
                            throw Located(error, statement);
                        }

                        var handler = NextOnError(statements, pc);
                        if (handler < 0)
                        {
                            throw Located(error, statement);
                        }

                        _handlingError = true;
                        pc = handler;
                        continue;
                    }
                    catch (Ipc.Core.Messages.CpfException error) when (error.MessageId != "CPF1241" && statement.Location is { })
                    {
                        if (statement.Opcode is RpgOpcode.Call or RpgOpcode.CallB or RpgOpcode.CallP)
                        {
                            context.SetFeedbackOperation(statement, routine, preserveCallTarget: true);
                            context.RecordStatus(202);
                            context.RecordFeedbackError(new RpgRuntimeException(error.Message) { StatusCode = 202, ServiceError = error });
                            var handler = MonitorHandler(statements, pc, 202, state);
                            if (handler >= 0) { pc = handler; continue; }
                            var programError = new RpgRuntimeException(error.Message) { StatusCode = 202, ServiceError = error, FeedbackRecorded = true };
                            if (HasEnclosingMonitor(programError)) throw Located(programError, statement);
                            if (InvokeErrorSubroutine(programError, context)) { pc++; continue; }
                            throw Located(programError, statement);
                        }
                        throw LocatedService(error, statement.Location);
                    }

                    if (jump >= 0)
                    {
                        while (state.Loops.TryPeek(out var loop) && (jump < loop.Start || jump > loop.End)) state.Loops.Pop();
                        while (state.Selects.TryPeek(out var select) && (jump < select.Start || jump > select.End)) state.Selects.Pop();
                    }
                    pc = jump >= 0 ? jump : pc + 1;
                }
                catch (ErrorRoutineBranch branch) when (ReferenceEquals(branch.Target, frame))
                {
                    pc = branch.Index;
                    while (state.Loops.TryPeek(out var loop) && (pc < loop.Start || pc > loop.End)) state.Loops.Pop();
                    while (state.Selects.TryPeek(out var select) && (pc < select.Start || pc > select.End)) state.Selects.Pop();
                }
            }
        }
        finally { _executionFrames.Pop(); }
    }

    private static RpgRuntimeException Located(RpgRuntimeException error, RpgStatement statement)
        => error.Location is null && statement.Location is { } location ? new(location, error.Message)
            { StatusCode = error.StatusCode, FileError = error.FileError, StatusFile = error.StatusFile, ServiceError = error.ServiceError,
                FeedbackRecorded = error.FeedbackRecorded, ErrorHandlerInvocations = error.ErrorHandlerInvocations,
                SuppressErrorSubroutines = error.SuppressErrorSubroutines } : error;
    private static Ipc.Core.Messages.CpfException LocatedService(Ipc.Core.Messages.CpfException error, Ipc.Core.Compilation.SourceLocation? location)
    {
        if (location is null) return error;
        var result = new Ipc.Core.Messages.CpfException(error.MessageId, $"{location}: {error.Message}", error.MessageType, error.Severity);
        foreach (System.Collections.DictionaryEntry entry in error.Data) result.Data[entry.Key] = entry.Value;
        return result;
    }

    private static int NextOnError(IReadOnlyList<RpgStatement> statements, int after)
    {
        for (var index = after + 1; index < statements.Count; index++)
        {
            if (statements[index].Opcode == RpgOpcode.OnError && statements[index].End < 0)
            {
                return index;
            }
        }

        return -1;
    }

    private int ExecuteOp(RpgStatement stmt, IReadOnlyList<RpgStatement> statements, RpgRuntimeContext ctx, RpgBlockState state)
    {
        switch (stmt.Opcode)
        {
            case RpgOpcode.Monitor:
            case RpgOpcode.EndMon:
                return -1;
            case RpgOpcode.Klist:
            case RpgOpcode.Kfld:
                throw new RpgRuntimeException("Key declarations must be bound by the compiler before execution.");
            case RpgOpcode.Add:
                return AssignResult(stmt, ctx, RpgValues.Add(EvalLeft(stmt, ctx), Eval(stmt.Factor2, ctx)));
            case RpgOpcode.Subtract:
                return AssignResult(stmt, ctx, RpgValues.Subtract(EvalLeft(stmt, ctx), Eval(stmt.Factor2, ctx)));
            case RpgOpcode.Multiply:
                return AssignResult(stmt, ctx, RpgValues.Multiply(EvalLeft(stmt, ctx), Eval(stmt.Factor2, ctx)));
            case RpgOpcode.Divide:
                return AssignResult(stmt, ctx, RpgValues.Divide(EvalLeft(stmt, ctx), Eval(stmt.Factor2, ctx)));
            case RpgOpcode.Mvr:
                return AssignResult(stmt, ctx, RpgValues.Modulus(EvalLeft(stmt, ctx), Eval(stmt.Factor2, ctx)));
            case RpgOpcode.ZAdd:
                return AssignResult(stmt, ctx, RpgValues.Positive(Eval(stmt.Factor2, ctx)));
            case RpgOpcode.ZSub:
                return AssignResult(stmt, ctx, RpgValues.Negate(Eval(stmt.Factor2, ctx)));
            case RpgOpcode.Eval:
                return EvalOpcode(stmt, ctx);
            case RpgOpcode.Move:
                return AssignResult(stmt, ctx, Eval(!string.IsNullOrWhiteSpace(stmt.Factor1) ? stmt.Factor1 : stmt.Factor2, ctx));
            case RpgOpcode.MoveL:
                return AssignResult(stmt, ctx, Eval(!string.IsNullOrWhiteSpace(stmt.Factor1) ? stmt.Factor1 : stmt.Factor2, ctx));
            case RpgOpcode.MoveA:
                return MoveArray(stmt, ctx);
            case RpgOpcode.Clear:
                var clearTarget = stmt.Result ?? stmt.Factor2;
                if (clearTarget is not null)
                {
                    ctx.ClearValue(clearTarget);
                }

                return -1;
            case RpgOpcode.BitOn:
                return SetBit(stmt, ctx, true);
            case RpgOpcode.BitOff:
                return SetBit(stmt, ctx, false);
            case RpgOpcode.SetOn:
                SetIndicators(stmt.Factor1, ctx, true);
                return -1;
            case RpgOpcode.SetOff:
                SetIndicators(stmt.Factor1, ctx, false);
                return -1;
            case RpgOpcode.Test:
                return TestOpcode(stmt, ctx);
            case RpgOpcode.Comp:
                return CompareOpcode(stmt, ctx);
            case RpgOpcode.If:
                return IfOpcode(stmt, ctx);
            case RpgOpcode.Else:
                return stmt.Jump >= 0 ? stmt.Jump : -1;
            case RpgOpcode.EndIf:
                return -1;
            case RpgOpcode.Select:
                state.Selects.Push(new RpgSelectFrame { Start = SelfIndex(statements, stmt), End = stmt.End });
                return -1;
            case RpgOpcode.When:
                return WhenOpcode(stmt, ctx, state);
            case RpgOpcode.Other:
                return OtherOpcode(stmt, state);
            case RpgOpcode.EndSl:
                if (state.Selects.Count > 0) state.Selects.Pop();
                return -1;
            case RpgOpcode.Do:
                return DoOpcode(stmt, statements, ctx, state);
            case RpgOpcode.DoU:
            case RpgOpcode.DoW:
                return DoConditionalOpcode(stmt, statements, ctx, state);
            case RpgOpcode.For:
                return ForOpcode(stmt, statements, ctx, state);
            case RpgOpcode.EndDo:
                return EndDoOpcode(stmt, ctx, state);
            case RpgOpcode.Iter:
                return IterOpcode(stmt, ctx, state);
            case RpgOpcode.Leave:
                return LeaveOpcode(state);
            case RpgOpcode.Goto:
                return GotoOpcode(stmt, statements);
            case RpgOpcode.Tag:
                return -1;
            case RpgOpcode.BegSr:
                return stmt.Jump >= 0 ? stmt.Jump : -1;
            case RpgOpcode.EndSr:
                return -1;
            case RpgOpcode.Exsr:
                return ExsrOpcode(stmt, ctx);
            case RpgOpcode.Retrn:
            case RpgOpcode.Return:
                if (_depth == 0)
                {
                    _programEnded = true;
                }
                else
                {
                    _returned = true;
                }

                return -1;
            case RpgOpcode.Open:
                FileIo(StatusFile(stmt), () => { if (!OpenWorkstation(stmt, ctx) && !OpenDisk(stmt, ctx)) OpenOpcode(stmt); }, 1217);
                ctx.RecordStatus(0, StatusFile(stmt));
                return -1;
            case RpgOpcode.Close:
                FileIo(StatusFile(stmt), () => { if (!CloseWorkstation(stmt, ctx) && !CloseDisk(stmt, ctx)) CloseOpcode(stmt); }, 1217);
                ctx.RecordStatus(0, StatusFile(stmt));
                return -1;
            case RpgOpcode.Unlock:
                FileIo(StatusFile(stmt), () => UnlockOpcode(stmt, ctx));
                ctx.RecordStatus(0, StatusFile(stmt));
                return -1;
            case RpgOpcode.Force:
                _ = GetCursor(FirstFile(stmt));
                return -1;
            case RpgOpcode.Chain:
                if (WorkstationIo(stmt, ctx, RpgWorkstationOperation.Chain)) return -1;
                return ChainOpcode(stmt, ctx);
            case RpgOpcode.Read:
                if (WorkstationIo(stmt, ctx, RpgWorkstationOperation.Read)) return -1;
                return ReadOpcode(stmt, ctx, false, false);
            case RpgOpcode.ReadC:
                if (!WorkstationIo(stmt, ctx, RpgWorkstationOperation.ReadChanged)) throw new RpgRuntimeException("READC requires a declared workstation subfile.");
                return -1;
            case RpgOpcode.ReadP:
                return ReadOpcode(stmt, ctx, true, false);
            case RpgOpcode.Reade:
                return ReadOpcode(stmt, ctx, false, true);
            case RpgOpcode.ReadPe:
                return ReadOpcode(stmt, ctx, true, true);
            case RpgOpcode.SetLl:
                return SetLimitOpcode(stmt, ctx, lower: true);
            case RpgOpcode.SetGt:
                return SetLimitOpcode(stmt, ctx, lower: false);
            case RpgOpcode.Write:
                if (WorkstationIo(stmt, ctx, RpgWorkstationOperation.Write)) return -1;
                return WriteOpcode(stmt, ctx);
            case RpgOpcode.Update:
                if (WorkstationIo(stmt, ctx, RpgWorkstationOperation.Update)) return -1;
                return UpdateOpcode(stmt, ctx);
            case RpgOpcode.Delete:
                return DeleteOpcode(stmt, ctx);
            case RpgOpcode.ExFmt:
                return ExFmtOpcode(stmt, ctx);
            case RpgOpcode.Dsply:
                return DsplyOpcode(stmt, ctx);
            case RpgOpcode.Call:
                return CallOpcode(stmt, ctx);
            case RpgOpcode.CallB:
            case RpgOpcode.CallP:
                return CallBOpcode(stmt, ctx);
            case RpgOpcode.Parm:
            case RpgOpcode.Plist:
                return -1;
            case RpgOpcode.SndPgMmsg:
            case RpgOpcode.SndMsg:
                return SendMessageOpcode(stmt, ctx);
            case RpgOpcode.RcvMsg:
                return RcvMessageOpcode(stmt, ctx);
            default:
                return -1;
        }
    }

    private object? Eval(string? text, RpgRuntimeContext ctx) =>
        string.IsNullOrWhiteSpace(text) ? null : RpgExpressionParser.Parse(text).Eval(ctx);

    private object? EvalLeft(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var value = Eval(stmt.Factor1, ctx);
        if (value is not null || stmt.Result is null)
        {
            return value;
        }

        return ctx.ReadValue(stmt.Result);
    }

    private int AssignResult(RpgStatement stmt, RpgRuntimeContext ctx, object? value)
    {
        if (stmt.Result is not null)
        {
            WriteTarget(stmt.Result, value, ctx);
            ApplyArithmeticIndicators(stmt, value, ctx);
        }

        return -1;
    }

    private void ApplyArithmeticIndicators(RpgStatement stmt, object? value, RpgRuntimeContext ctx)
    {
        if (stmt.Indicator1 == 0 && stmt.Indicator2 == 0 && stmt.Indicator3 == 0) return;
        setInd(stmt.Indicator1, RpgValues.Compare(value, 0m) >= 0, ctx);
        setInd(stmt.Indicator2, RpgValues.Compare(value, 0m) <= 0, ctx);
        if (RpgValues.Compare(value, 0m) == 0)
        {
            setInd(stmt.Indicator3, true, ctx);
        }

        static void setInd(int indicator, bool value, RpgRuntimeContext context)
        {
            if (indicator != 0)
            {
                context.Indicators[indicator] = value;
            }
        }
    }

    private int EvalOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        string target;
        string expression;
        if (!string.IsNullOrWhiteSpace(stmt.Result))
        {
            target = stmt.Result!;
            expression = stmt.Factor1 ?? string.Empty;
        }
        else if (stmt.Value is not null && SplitAssignment(stmt.Value, out target, out expression))
        {
        }
        else
        {
            target = stmt.Factor1 ?? string.Empty;
            expression = stmt.Factor2 ?? string.Empty;
        }

        var value = Eval(expression, ctx);
        if (target.Length > 0)
        {
            WriteTarget(target, value, ctx);
        }

        ApplyArithmeticIndicators(stmt, value, ctx);
        return -1;
    }

    private static bool SplitAssignment(string text, out string target, out string expression)
    {
        var quote = false;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (c == '\'')
            {
                quote = !quote;
                continue;
            }

            if (c == '=' && !quote)
            {
                target = text[..index].Trim();
                expression = text[(index + 1)..].Trim();
                return true;
            }
        }

        target = string.Empty;
        expression = string.Empty;
        return false;
    }

    private void WriteTarget(string target, object? value, RpgRuntimeContext ctx)
    {
        var expr = RpgExpressionParser.Parse(target);
        switch (expr)
        {
            case RpgFieldRef fieldRef:
                ctx.WriteValue(fieldRef.Name, value);
                break;
            case RpgDynamicIndexRef indexRef:
                ctx.WriteArrayValue(indexRef.Name, RpgValues.ToLong(indexRef.Index.Eval(ctx)), value);
                break;
            case RpgMemberRef or RpgElementRef:
                ctx.WriteNativeReference(expr, value);
                break;
            case RpgIndicatorRef indicatorRef:
                ctx.Indicators[indicatorRef.Indicator] = RpgValues.ToBool(value);
                break;
            case RpgBuiltin { Name: "SUBST" } substring:
                ctx.WriteSubstring(substring.Arguments, value);
                break;
            default:
                throw new RpgRuntimeException($"Invalid assignment target '{target}'.");
        }
    }

    private int MoveArray(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var source = stmt.Factor2;
        var destination = stmt.Result;
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
        {
            throw new RpgRuntimeException("MOVA requires a source and a target array.");
        }

        var sourceField = stmt.Factor2!.StartsWith('*') ? null : ctx.Program.FindField(source);
        var destField = ctx.Program.FindField(destination);
        var count = destField?.IsArray == true ? destField.Dimension : 1;

        for (var index = 1; index <= count; index++)
        {
            ctx.WriteArrayValue(destination, index, ctx.ReadArrayValue(source, index));
        }

        return -1;
    }

    private int SetBit(RpgStatement stmt, RpgRuntimeContext ctx, bool on)
    {
        var targetName = stmt.Result ?? stmt.Factor1;
        var bitText = !string.IsNullOrWhiteSpace(stmt.Factor2)
            ? stmt.Factor2
            : !string.IsNullOrWhiteSpace(stmt.Factor1)
                ? stmt.Factor1
                : stmt.Length;
        var target = ctx.Program.FindField(targetName!)
            ?? throw new RpgRuntimeException($"Unknown field '{targetName}'.");
        if (target.Kind is not (RpgFieldKind.Character or RpgFieldKind.VaryingChar))
        {
            throw new RpgRuntimeException($"BITON/BITOFF target '{targetName}' must be character.");
        }

        var bit = int.TryParse(bitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new RpgRuntimeException($"Invalid bit number '{bitText}'.");
        if (bit < 1 || bit > target.Length * 8)
        {
            throw new RpgRuntimeException($"Bit number {bit} out of range for field '{targetName}'.");
        }

        var bytes = System.Text.Encoding.ASCII.GetBytes(RpgValues.ToText(ctx.ReadValue(targetName!)).PadRight(target.Length)[..target.Length]);
        var byteIndex = (bit - 1) / 8;
        var bitIndex = (bit - 1) % 8;
        var mask = (byte)(1 << (7 - bitIndex));
        bytes[byteIndex] = on ? (byte)(bytes[byteIndex] | mask) : (byte)(bytes[byteIndex] & ~mask);
        ctx.WriteValue(targetName!, System.Text.Encoding.ASCII.GetString(bytes));
        return -1;
    }

    private int TestOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var target = stmt.Factor1 ?? stmt.Result;
        if (target is not null && ctx.HasField(target))
        {
            _ = RpgValues.ToDecimal(ctx.ReadValue(target));
        }

        return -1;
    }

    private int CompareOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var comparison = RpgValues.Compare(Eval(stmt.Factor1, ctx), Eval(stmt.Factor2, ctx));
        if (stmt.Indicator1 != 0)
        {
            ctx.Indicators[stmt.Indicator1] = comparison < 0;
        }

        if (stmt.Indicator2 != 0)
        {
            ctx.Indicators[stmt.Indicator2] = comparison == 0;
        }

        if (stmt.Indicator3 != 0)
        {
            ctx.Indicators[stmt.Indicator3] = comparison > 0;
        }

        if (stmt.Result is not null)
        {
            ctx.WriteValue(stmt.Result, comparison);
        }

        return -1;
    }

    private int IfOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var conditionText = stmt.Factor1 ?? stmt.Value;
        if (string.IsNullOrWhiteSpace(conditionText))
        {
            throw new RpgRuntimeException("IF requires a condition.");
        }

        var condition = RpgValues.ToBool(Eval(conditionText, ctx));
        if (condition)
        {
            return -1;
        }

        return stmt.Jump >= 0 ? stmt.Jump : -1;
    }

    private int WhenOpcode(RpgStatement stmt, RpgRuntimeContext ctx, RpgBlockState state)
    {
        var frame = state.Selects.TryPeek(out var current) ? current : throw new RpgRuntimeException("WHEN outside an active SELECT.");
        if (frame.Matched)
        {
            return frame.End;
        }

        var condition = RpgValues.ToBool(Eval(stmt.Factor1 ?? stmt.Value, ctx));
        if (condition)
        {
            frame.Matched = true;
            return -1;
        }

        return stmt.Jump >= 0 ? stmt.Jump : -1;
    }

    private int OtherOpcode(RpgStatement stmt, RpgBlockState state)
    {
        var frame = state.Selects.TryPeek(out var current) ? current : throw new RpgRuntimeException("OTHER outside an active SELECT.");
        if (frame.Matched)
        {
            return frame.End;
        }

        frame.Matched = true;
        return -1;
    }

    private int DoOpcode(RpgStatement stmt, IReadOnlyList<RpgStatement> statements, RpgRuntimeContext ctx, RpgBlockState state)
    {
        var counter = stmt.Result
            ?? throw new RpgRuntimeException("DO requires a result counter field.");
        var start = RpgValues.ToDecimal(Eval(stmt.Factor1, ctx));
        var end = RpgValues.ToDecimal(Eval(stmt.Factor2, ctx));
        ctx.WriteValue(counter, start);
        state.Loops.Push(new RpgLoopFrame
        {
            Start = SelfIndex(statements, stmt),
            End = stmt.End,
            Kind = RpgLoopKind.Do,
            Counter = counter,
            EndValue = end,
            Step = 1m,
        });
        return -1;
    }

    private int DoConditionalOpcode(RpgStatement stmt, IReadOnlyList<RpgStatement> statements, RpgRuntimeContext ctx, RpgBlockState state)
    {
        var self = SelfIndex(statements, stmt);
        var condition = RpgValues.ToBool(Eval(stmt.Factor1 ?? stmt.Value, ctx));
        if (stmt.Opcode == RpgOpcode.DoW)
        {
            if (!condition)
            {
                if (state.Loops.Count > 0 && state.Loops.Peek().Start == self)
                {
                    state.Loops.Pop();
                }

                return stmt.End + 1;
            }

            if (state.Loops.Count == 0 || state.Loops.Peek().Start != self)
            {
                state.Loops.Push(new RpgLoopFrame
                {
                    Start = self,
                    End = stmt.End,
                    Kind = RpgLoopKind.DoW,
                    Counter = null,
                    Condition = ParseSafe(stmt.Factor1 ?? stmt.Value),
                });
            }

            return -1;
        }

        state.Loops.Push(new RpgLoopFrame
        {
            Start = self,
            End = stmt.End,
            Kind = RpgLoopKind.DoU,
            Counter = null,
            Condition = ParseSafe(stmt.Factor1 ?? stmt.Value),
        });
        return -1;
    }

    private int ForOpcode(RpgStatement stmt, IReadOnlyList<RpgStatement> statements, RpgRuntimeContext ctx, RpgBlockState state)
    {
        var text = stmt.Value ?? string.Empty;
        if (!TryParseFor(text, out var counter, out var startExpr, out var endExpr, out var byExpr))
        {
            throw new RpgRuntimeException($"Invalid FOR statement '{text}'.");
        }

        var step = RpgValues.ToDecimal(Eval(byExpr, ctx) ?? 1m);
        var start = RpgValues.ToDecimal(Eval(startExpr, ctx));
        var end = RpgValues.ToDecimal(Eval(endExpr, ctx));
        ctx.WriteValue(counter, start);
        if (step > 0 && start > end || step < 0 && start < end) return stmt.End + 1;
        state.Loops.Push(new RpgLoopFrame
        {
            Start = SelfIndex(statements, stmt),
            End = stmt.End,
            Kind = RpgLoopKind.For,
            Counter = counter,
            EndValue = end,
            Step = step,
        });
        return -1;
    }

    private int EndDoOpcode(RpgStatement stmt, RpgRuntimeContext ctx, RpgBlockState state)
    {
        if (state.Loops.Count == 0)
        {
            throw new RpgRuntimeException("ENDDO without a matching DO/FOR.");
        }

        var frame = state.Loops.Peek();
        switch (frame.Kind)
        {
            case RpgLoopKind.Do:
            case RpgLoopKind.For:
            {
                var current = RpgValues.ToDecimal(ctx.ReadValue(frame.Counter!)) + frame.Step;
                ctx.WriteValue(frame.Counter!, current);
                var done = frame.Step >= 0 ? current > frame.EndValue : current < frame.EndValue;
                if (done)
                {
                    state.Loops.Pop();
                    return -1;
                }

                return frame.Start + 1;
            }

            case RpgLoopKind.DoU:
            {
                var condition = RpgValues.ToBool(frame.Condition?.Eval(ctx));
                if (condition)
                {
                    state.Loops.Pop();
                    return -1;
                }

                return frame.Start + 1;
            }

            case RpgLoopKind.DoW:
                return frame.Start;

            default:
                return -1;
        }
    }

    private static int IterOpcode(RpgStatement stmt, RpgRuntimeContext ctx, RpgBlockState state)
    {
        if (state.Loops.Count == 0)
        {
            throw new RpgRuntimeException("ITER outside of a loop.");
        }

        var frame = state.Loops.Peek();
        return frame.Kind == RpgLoopKind.DoW ? frame.Start : frame.End;
    }

    private static int LeaveOpcode(RpgBlockState state)
    {
        if (state.Loops.Count == 0)
        {
            throw new RpgRuntimeException("LEAVE outside of a loop.");
        }

        return state.Loops.Pop().End + 1;
    }

    private int GotoOpcode(RpgStatement stmt, IReadOnlyList<RpgStatement> statements)
    {
        var label = stmt.Factor1;
        for (var index = 0; index < statements.Count; index++)
        {
            if (statements[index].Opcode == RpgOpcode.Tag &&
                string.Equals(statements[index].Factor1, label, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        BranchFromErrorRoutine(label, statements);
        throw new RpgRuntimeException($"GOTO label '{label}' not found.");
    }

    private int ExsrOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var name = stmt.Factor1
            ?? throw new RpgRuntimeException("EXSR requires a subroutine name.");
        var subroutine = ctx.Program.FindSubroutine(name.Trim('\''), _procedure)
            ?? throw new RpgRuntimeException($"Subroutine '{name}' not found.");
        ExecuteSubroutine(subroutine, ctx);
        return -1;
    }

    private void OpenOpcode(RpgStatement stmt)
    {
        var fileName = stmt.Factor2 ?? stmt.Factor1
            ?? throw new RpgRuntimeException("OPEN requires a file name.");
        _ = GetCursor(fileName);
    }

    private void CloseOpcode(RpgStatement stmt)
    {
        var fileName = (stmt.Factor2 ?? stmt.Factor1)?.Trim().ToUpperInvariant();
        if (fileName is not null)
        {
            if (_fileHandles.Remove(fileName, out var handle)) handle.Close();
            _files.Remove(fileName);
        }
    }

    private RpgFileCursor GetCursor(string fileName)
    {
        var key = fileName.Trim().ToUpperInvariant();
        if (DeclaredDisk(key) is { } declared) return declared;
        if (_files.TryGetValue(key, out var existing))
        {
            return _fileHandles.TryGetValue(key, out var handle) ? handle.Cursor : existing;
        }

        if (_host.OpenFile is { } openFile)
        {
            var handle = openFile(key);
            try { var opened = handle.Cursor; _fileHandles.Add(key, handle); _files.Add(key, opened); return opened; }
            catch { handle.Dispose(); throw; }
        }

        if (_host.Files is null)
        {
            throw new RpgRuntimeException($"No file access is configured for program usage.");
        }

        var library = ResolveLibrary(fileName);
        var cursor = new RpgFileCursor(_host.Files, library, fileName, fileMember(fileName), _host.CancellationToken);
        _files[key] = cursor;
        _fileHandles[key] = new RpgFileHandle(() => cursor, cursor.Dispose);
        return cursor;
    }

    private string ResolveLibrary(string fileName) =>
        _host.LibraryResolver?.Invoke("*LIBL", fileName)
        ?? throw new RpgRuntimeException($"File '{fileName}' is not available.");

    private static string fileMember(string fileName) => fileName.Trim().ToUpperInvariant();

    private int ChainOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var cursor = IoCursor(stmt);
        var search = cursor.IsKeyed ? FileSearchValues(stmt, ctx, cursor) ?? throw new RpgRuntimeException("CHAIN requires a search argument.") : null;
        var number = cursor.IsKeyed ? 0 : FileRecordNumber(stmt, ctx);
        var found = FileIo(StatusFile(stmt), () => cursor.IsKeyed ? cursor.Chain(search!, !stmt.NoLock) : cursor.ChainRecord(number, !stmt.NoLock));
        ctx.RecordStatus(found ? 0 : 12, StatusFile(stmt));
        ctx.SetFileStatus(StatusFile(stmt), found: found, clearFileEof: found);
        var declared = DiskBinding(ctx, stmt) is not null;
        var indicator1 = declared ? stmt.Indicator1 : stmt.Indicator1 != 0 ? stmt.Indicator1 : 1;
        var indicator2 = declared ? 0 : stmt.Indicator2 != 0 ? stmt.Indicator2 : stmt.Indicator1 == 0 ? 2 : 0;
        if (indicator1 != 0)
        {
            ctx.Indicators[indicator1] = declared ? !found : found;
        }

        if (indicator2 != 0)
        {
            ctx.Indicators[indicator2] = !found;
        }

        if (found)
        {
            CopyDiskRecord(ctx, stmt, cursor.Current!, cursor);
        }

        return -1;
    }

    private int ReadOpcode(RpgStatement stmt, RpgRuntimeContext ctx, bool prior, bool keyed)
    {
        var cursor = IoCursor(stmt);
        IReadOnlyDictionary<string, object?>? record = null;
        bool eof = false;
        if (keyed)
        {
            if (!cursor.IsKeyed) throw new RpgRuntimeException("READE/READPE requires a keyed file.");
            var prefix = string.IsNullOrWhiteSpace(stmt.Factor2) && stmt.FileSearch is null ? null : FileSearchValues(stmt, ctx, cursor);

            if (prefix is { Count: 0 })
            {
                record = null;
                eof = true;
            }
            else if (prior)
            {
                FileIo(StatusFile(stmt), () => { cursor.ReadPriorKeyed(prefix, out record, out eof, !stmt.NoLock); });
            }
            else
            {
                FileIo(StatusFile(stmt), () => { cursor.ReadNextKeyed(prefix, out record, out eof, !stmt.NoLock); });
            }
        }
        else if (prior)
        {
            FileIo(StatusFile(stmt), () => { cursor.ReadPrior(out record, out eof, !stmt.NoLock); });
        }
        else
        {
            FileIo(StatusFile(stmt), () => { cursor.ReadNext(out record, out eof, !stmt.NoLock); });
        }

        ctx.RecordStatus(eof ? 11 : 0, StatusFile(stmt));
        ctx.SetFileStatus(StatusFile(stmt), eof: eof);
        var declared = DiskBinding(ctx, stmt) is not null;
        var indicator1 = declared ? stmt.Indicator1 : stmt.Indicator1 != 0 ? stmt.Indicator1 : 1;
        var indicator2 = declared ? 0 : stmt.Indicator2 != 0 ? stmt.Indicator2 : stmt.Indicator1 == 0 ? 2 : 0;
        if (indicator1 != 0 && !declared)
        {
            ctx.Indicators[indicator1] = !eof;
        }

        if (indicator2 != 0)
        {
            ctx.Indicators[indicator2] = eof;
        }

        if (declared && stmt.Indicator3 != 0) ctx.Indicators[stmt.Indicator3] = eof;
        if (record is not null)
        {
            CopyDiskRecord(ctx, stmt, record, cursor);
        }

        return -1;
    }

    private int SetLimitOpcode(RpgStatement stmt, RpgRuntimeContext ctx, bool lower)
    {
        var cursor = IoCursor(stmt);
        var search = cursor.IsKeyed ? FileSearchValues(stmt, ctx, cursor) ?? throw new RpgRuntimeException("SETLL/SETGT requires a search argument.") : null;
        var number = cursor.IsKeyed ? 0 : FileRecordNumber(stmt, ctx);
        if (lower)
        {
            var equal = FileIo(StatusFile(stmt), () => cursor.IsKeyed ? cursor.SetLowerBound(search!) : cursor.SetRecordLowerBound(number));
            ctx.RecordStatus(cursor.AtEnd ? 12 : 0, StatusFile(stmt));
            ctx.SetFileStatus(StatusFile(stmt), found: !cursor.AtEnd, clearFileEof: !cursor.AtEnd);
            if (stmt.Indicator1 != 0)
            {
                ctx.Indicators[stmt.Indicator1] = equal;
            }
        }
        else
        {
            FileIo(StatusFile(stmt), () => { if (cursor.IsKeyed) cursor.SetUpperBound(search!); else cursor.SetRecordUpperBound(number); });
            ctx.RecordStatus(cursor.AtEnd ? 12 : 0, StatusFile(stmt));
            ctx.SetFileStatus(StatusFile(stmt), found: !cursor.AtEnd, clearFileEof: !cursor.AtEnd);
        }

        return -1;
    }

    private int WriteOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var binding = DiskBinding(ctx, stmt);
        if (binding is not null)
        {
            if (!binding.Request.Output || !FirstFile(stmt).Equals(binding.Layout.Record, StringComparison.OrdinalIgnoreCase))
                throw new RpgRuntimeException("WRITE requires an output-capable DISK record format.");
            var target = IoCursor(stmt);
            var mapped = binding.Layout.Fields.ToDictionary(field => field.External, field => ctx.ReadValue(field.Name), StringComparer.OrdinalIgnoreCase);
            FileIo(binding.Request.Name, () => target.Write(mapped));
            ctx.RecordStatus(0, binding.Request.Name);
            return -1;
        }
        var cursor = IoCursor(stmt);
        var format = stmt.Factor1;
        if (string.IsNullOrWhiteSpace(format))
        {
            format = cursor.Format;
        }

        var values = BuildRecordValues(ctx, cursor.Definition.Formats.First(f => string.Equals(f.Name, format, StringComparison.OrdinalIgnoreCase)));
        FileIo(StatusFile(stmt), () => _host.Files!.Insert(cursor.Library, cursor.Name, cursor.Member, format, values));
        cursor.Reload();
        ctx.RecordStatus(0, StatusFile(stmt));
        return -1;
    }

    private int UpdateOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var binding = DiskBinding(ctx, stmt);
        if (binding is not null && (!binding.Request.Update || !FirstFile(stmt).Equals(binding.Layout.Record, StringComparison.OrdinalIgnoreCase)))
            throw new RpgRuntimeException("UPDATE requires an update-capable DISK record format.");
        var cursor = IoCursor(stmt);
        var values = binding is null ? BuildRecordValues(ctx, cursor.Definition.PrimaryFormat) :
            binding.Layout.Fields.ToDictionary(field => field.External, field => ctx.ReadValue(field.Name), StringComparer.OrdinalIgnoreCase);
        FileIo(StatusFile(stmt), () => cursor.UpdateCurrent(values));
        ctx.RecordStatus(0, StatusFile(stmt));
        return -1;
    }

    private int DeleteOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var binding = DiskBinding(ctx, stmt);
        if (binding is not null && !binding.Request.Delete)
            throw new RpgRuntimeException("DELETE requires delete access in the DISK usage declaration.");
        var cursor = IoCursor(stmt);
        bool found;
        if (stmt.FileSearch is null) { FileIo(StatusFile(stmt), cursor.DeleteCurrent); found = true; }
        else
        {
            var search = cursor.IsKeyed ? FileSearchValues(stmt, ctx, cursor) ?? throw new RpgRuntimeException("DELETE requires a search argument.") : null;
            var number = cursor.IsKeyed ? 0 : FileRecordNumber(stmt, ctx);
            found = FileIo(StatusFile(stmt), () => cursor.IsKeyed ? cursor.DeleteKey(search!) : cursor.DeleteRecord(number));
        }
        ctx.SetFileStatus(StatusFile(stmt), found: found);
        ctx.RecordStatus(0, StatusFile(stmt));
        if (stmt.Indicator1 != 0) ctx.Indicators[stmt.Indicator1] = !found;
        return -1;
    }

    private int ExFmtOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        if (!WorkstationIo(stmt, ctx, RpgWorkstationOperation.Exchange)) throw new RpgRuntimeException("EXFMT requires a declared WORKSTN record.");
        return -1;
    }

    private int DsplyOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var text = Eval(stmt.Factor1, ctx);
        var display = RpgValues.ToText(text);
        _host.Display?.Invoke(display);
        var input = _host.ReceiveMessage?.Invoke();
        if (input is not null && stmt.Result is not null)
        {
            ctx.WriteValue(stmt.Result, input);
        }

        return -1;
    }

    private int SendMessageOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var text = Eval(stmt.Factor1, ctx) ?? Eval(stmt.Factor2, ctx);
        _host.SendMessage?.Invoke(RpgValues.ToText(text));
        return -1;
    }

    private int RcvMessageOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var text = _host.ReceiveMessage?.Invoke();
        var target = stmt.Result ?? stmt.Factor2;
        if (text is not null && target is not null)
        {
            ctx.WriteValue(target, text);
        }

        return -1;
    }

    private RpgExternalCallResult? CallExternal(RpgStatement statement, string name, IReadOnlyList<object?> parameters,
        RpgRuntimeContext? context = null, IReadOnlyList<RpgExpr>? arguments = null, RpgPrototype? prototype = null,
        Func<IReadOnlyList<object?>>? prepareSemantic = null)
    {
        RpgArgumentScope? scope = null;
        context?.SetFeedbackCallTarget(name);
        using var semantic = prepareSemantic is null ? null : new SemanticArguments(prepareSemantic);
        var active = true;
        try
        {
            IReadOnlyList<object?> Borrow()
            {
                ObjectDisposedException.ThrowIf(!active, this);
                Func<int, RpgRuntimeContext.NativeView?>? captured = parameters is ArgumentSnapshots snapshots ? snapshots.NativeAt : null;
                return (scope ??= new RpgArgumentScope(context!, arguments!, parameters, _host.Ccsid(), prototype, captured)).Arguments;
            }
            var legacy = _host.TypedProgramCaller is null ? prepareSemantic?.Invoke() ?? parameters : parameters;
            var result = _host.TypedProgramCaller is { } typed
                ? typed("*LIBL", name, (IReadOnlyList<object?>?)semantic ?? parameters, context is not null && arguments is not null ? Borrow : null)
                : _host.ProgramCaller?.Invoke("*LIBL", name, legacy);
            if (semantic is not null && result is not null && ReferenceEquals(result.UpdatedParameters, semantic))
                result = new() { Success = result.Success, Message = result.Message, UpdatedParameters = semantic.ToArray() };
            if (result is { Success: false } && statement.Indicator1 == 0)
                throw new RpgRuntimeException(result.Message ?? $"External program {name} failed.");
            return result;
        }
        finally { active = false; scope?.Dispose(); }
    }

    private sealed class SemanticArguments(Func<IReadOnlyList<object?>> prepare) : IReadOnlyList<object?>, IDisposable
    {
        private Func<IReadOnlyList<object?>>? _prepare = prepare;
        private IReadOnlyList<object?>? _values;
        private IReadOnlyList<object?> Values => _values ??= (_prepare ?? throw new ObjectDisposedException(nameof(SemanticArguments)))();
        public int Count => Values.Count;
        public object? this[int index] => Values[index];
        public IEnumerator<object?> GetEnumerator() => Values.GetEnumerator();
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => _prepare = null;
    }

    private sealed class ArgumentSnapshots : IReadOnlyList<object?>, IDisposable
    {
        private readonly object?[] _values;
        private readonly RpgRuntimeContext.NativeView?[] _nativeViews;
        private readonly Dictionary<int, System.Runtime.ExceptionServices.ExceptionDispatchInfo> _errors = new();
        internal ArgumentSnapshots(IEnumerable<RpgExpr> expressions, RpgRuntimeContext context)
        {
            var source = expressions.ToArray(); _values = new object?[source.Length];
            _nativeViews = new RpgRuntimeContext.NativeView?[source.Length]; long nativeBytes = 0;
            for (var index = 0; index < source.Length; index++)
                if (context.TryNativeReference(source[index], out var view))
                {
                    _nativeViews[index] = view; nativeBytes += view.Width;
                    if (source.Length > 256 || nativeBytes > 1048576) throw new Ipc.Core.Messages.CpfException("CPF3C3A", "Native call arguments exceed 256 parameters or 1 MiB.");
                }
            for (var index = 0; index < source.Length; index++)
            {
                // A borrowed output receiver need not contain valid numeric data before the call.
                if (_nativeViews[index] is { } native)
                {
                    try { _values[index] = context.ReadNative(native); }
                    catch (RpgRuntimeException error) { _errors.Add(index, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error)); }
                }
                else _values[index] = source[index].Eval(context);
            }
        }
        public int Count => _values.Length;
        internal RpgRuntimeContext.NativeView? NativeAt(int index) => _nativeViews[index];
        public object? this[int index] { get { if (_errors.TryGetValue(index, out var error)) error.Throw(); return _values[index]; } }
        public IEnumerator<object?> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
        global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public void Dispose() => Array.Clear(_nativeViews);
    }

    private int CallOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var programName = Unquote(stmt.Factor1 ?? string.Empty);
        if (programName.Length == 0)
        {
            programName = Unquote(stmt.Result ?? string.Empty);
        }

        if (programName.Length == 0)
        {
            throw new RpgRuntimeException("CALL requires a program name.");
        }

        var plistName = stmt.Factor2;
        var parameters = CollectParameters(stmt, ctx);
        using var snapshots = parameters as ArgumentSnapshots;
        var result = CallExternal(stmt, programName, parameters, ctx,
            string.IsNullOrWhiteSpace(plistName) ? [] : CollectPlistFieldNames(ctx.Program, plistName).Select(RpgExpressionParser.Parse).ToArray());
        if (result is null)
        {
            if (stmt.Indicator1 != 0)
            {
                ctx.Indicators[stmt.Indicator1] = true;
            }
        }
        else
        {
            if (!result.Success)
            {
                if (stmt.Indicator1 != 0)
                {
                    ctx.Indicators[stmt.Indicator1] = true;
                }

                if (stmt.Result is not null)
                {
                    ctx.WriteValue(stmt.Result, result.Message ?? string.Empty);
                }
            }

            if (!string.IsNullOrWhiteSpace(plistName))
            {
                WriteBackPlist(ctx.Program, plistName, result.UpdatedParameters, ctx);
            }
        }

        return -1;
    }

    private int CallBOpcode(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var text = stmt.Factor1 ?? stmt.Value ?? string.Empty;
        var name = text;
        var arguments = new List<RpgExpr>();
        var rawArguments = new List<string>();
        var open = text.IndexOf('(');
        if (open >= 0)
        {
            name = text[..open].Trim();
            var inner = text[(open + 1)..];
            if (inner.EndsWith(')'))
            {
                inner = inner[..^1];
            }
            else throw new RpgRuntimeException("Unclosed RPG call argument list.");

            if (inner.Trim().Length > 0)
            {
                foreach (var argument in SplitCallArguments(inner))
                {
                    rawArguments.Add(argument);
                    arguments.Add(RpgExpressionParser.Parse(argument));
                }
            }
        }

        name = Unquote(name);
        var subroutine = ctx.Program.FindSubprocedure(name);
        if (subroutine is not null)
        {
            BindParameters(ctx, subroutine, arguments);
            var savedReturned = _returned;
            var savedHandling = _handlingError;
            _returned = false;
            _depth++;
            var savedCount = ctx.ParameterCount; ctx.ParameterCount = arguments.Count;
            var savedProcedure = _procedure; var savedInvocation = _procedureInvocation;
            _procedure = subroutine.Name; _procedureInvocation = new object();
            try { Execute(subroutine.Statements, ctx, new RpgBlockState(), subroutine.Name); }
            finally
            {
                ctx.ParameterCount = savedCount; _depth--;
                _procedure = savedProcedure; _procedureInvocation = savedInvocation;
                _returned = savedReturned; _handlingError = savedHandling;
            }
            return -1;
        }

        var plistName = stmt.Factor2;
        if (string.IsNullOrWhiteSpace(plistName) && rawArguments.Count == 1)
        {
            var candidate = rawArguments[0].Trim();
            if (IsPlistName(ctx.Program, candidate))
            {
                plistName = candidate;
                arguments = new List<RpgExpr>();
            }
        }

        if (!string.IsNullOrWhiteSpace(plistName))
        {
            var parameters = CollectPlistValues(ctx.Program, plistName, ctx);
            using var snapshots = parameters as ArgumentSnapshots;
            var result = CallExternal(stmt, name, parameters, ctx,
                CollectPlistFieldNames(ctx.Program, plistName).Select(n => (RpgExpr)new RpgFieldRef(n)).ToArray());
            if (result is not null)
            {
                if (!result.Success && stmt.Indicator1 != 0)
                {
                    ctx.Indicators[stmt.Indicator1] = true;
                }

                WriteBackPlist(ctx.Program, plistName, result.UpdatedParameters, ctx);
            }

            return -1;
        }

        using var evaluated = new ArgumentSnapshots(arguments, ctx);

        var prototype = ctx.Program.FindPrototype(name);
        if (prototype is not null)
        {
            var externalName = prototype.ExternalName ?? prototype.Name;
            IReadOnlyList<object?> PrepareSemantic()
            {
                BindPrototypeParameters(ctx, prototype, evaluated);
                return prototype.Parameters.Count > 0 ? prototype.Parameters.Select(ctx.ReadValue).ToArray() : evaluated.ToArray();
            }
            var result = CallExternal(stmt, externalName, evaluated, ctx, arguments, prototype, PrepareSemantic);
            if (result is not null)
            {
                if (!result.Success && stmt.Indicator1 != 0)
                {
                    ctx.Indicators[stmt.Indicator1] = true;
                }

                WriteBackParameters(prototype.Parameters, result.UpdatedParameters, ctx);
            }

            return -1;
        }

        var pointerField = ctx.Program.FindField(name);
        if (pointerField is { Kind: RpgFieldKind.ProcPtr })
        {
            var target = RpgValues.ToText(ctx.ReadValue(name));
            if (target.Length > 0)
            {
                var result = CallExternal(stmt, target, evaluated, ctx, arguments);
                if (result is not null)
                {
                    if (!result.Success && stmt.Indicator1 != 0)
                    {
                        ctx.Indicators[stmt.Indicator1] = true;
                    }

                    WriteBackArguments(arguments, result.UpdatedParameters, ctx, evaluated);
                }
            }

            return -1;
        }

        var external = CallExternal(stmt, name, evaluated, ctx, arguments);
        if (external is not null && !external.Success && stmt.Indicator1 != 0)
        {
            ctx.Indicators[stmt.Indicator1] = true;
        }

        WriteBackArguments(arguments, external?.UpdatedParameters, ctx, evaluated);
        return -1;
    }

    private static IEnumerable<string> SplitCallArguments(string text)
    {
        var start = 0; var depth = 0; var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (quoted && ch == '\\' && index + 1 < text.Length) { index++; continue; }
            if (ch == '\'')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '\'') { index++; continue; }
                quoted = !quoted;
            }
            else if (!quoted)
            {
                if (ch == '(') depth++;
                else if (ch == ')' && --depth < 0) throw new RpgRuntimeException("Unbalanced RPG call argument.");
                else if (ch == ':' && depth == 0)
                {
                    var part = text[start..index].Trim();
                    if (part.Length == 0) throw new RpgRuntimeException("Empty RPG call argument.");
                    yield return part; start = index + 1;
                }
            }
        }
        if (quoted || depth != 0) throw new RpgRuntimeException("Unclosed RPG call expression or literal.");
        var last = text[start..].Trim();
        if (last.Length == 0) throw new RpgRuntimeException("Empty RPG call argument.");
        yield return last;
    }

    private static void BindPrototypeParameters(RpgRuntimeContext ctx, RpgPrototype prototype, IReadOnlyList<object?> arguments)
    {
        for (var index = 0; index < prototype.Parameters.Count && index < arguments.Count; index++)
        {
            ctx.WriteValue(prototype.Parameters[index], arguments[index]);
        }
    }

    private static void WriteBackArguments(IReadOnlyList<RpgExpr> arguments, IReadOnlyList<object?>? updated, RpgRuntimeContext ctx, ArgumentSnapshots? snapshots = null)
    {
        if (updated is null)
        {
            return;
        }

        for (var index = 0; index < arguments.Count && index < updated.Count; index++)
        {
            if (updated[index] is not null && snapshots?.NativeAt(index) is { } native) ctx.WriteCapturedNative(native, updated[index]);
            else if (arguments[index] is RpgFieldRef field && updated[index] is not null)
            {
                ctx.WriteValue(field.Name, updated[index]);
            }
            else if (updated[index] is not null && ctx.TryNativeReference(arguments[index], out _)) ctx.WriteNativeReference(arguments[index], updated[index]);
        }
    }

    private static void WriteBackParameters(IReadOnlyList<string> parameters, IReadOnlyList<object?>? updated, RpgRuntimeContext ctx)
    {
        if (updated is null)
        {
            return;
        }

        for (var index = 0; index < parameters.Count && index < updated.Count; index++)
        {
            if (updated[index] is not null)
            {
                ctx.WriteValue(parameters[index], updated[index]);
            }
        }
    }

    private static bool IsPlistName(RpgProgram program, string name) =>
        program.MainStatements.Any(s => s.Opcode == RpgOpcode.Plist &&
            string.Equals(s.Factor1, name, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<object?> CollectPlistValues(RpgProgram program, string plistName, RpgRuntimeContext ctx)
    {
        var names = CollectPlistFieldNames(program, plistName);
        return new ArgumentSnapshots(names.Select(n => (RpgExpr)new RpgFieldRef(n)), ctx);
    }

    private static IReadOnlyList<string> CollectPlistFieldNames(RpgProgram program, string plistName)
    {
        var names = new List<string>();
        var collecting = false;
        foreach (var candidate in program.MainStatements)
        {
            if (candidate.Opcode == RpgOpcode.Plist &&
                string.Equals(candidate.Factor1, plistName, StringComparison.OrdinalIgnoreCase))
            {
                collecting = true;
                continue;
            }

            if (collecting)
            {
                if (candidate.Opcode != RpgOpcode.Parm || string.IsNullOrEmpty(candidate.Factor1))
                {
                    break;
                }

                names.Add(candidate.Factor1);
            }
        }

        return names;
    }

    private static void WriteBackPlist(RpgProgram program, string plistName, IReadOnlyList<object?>? updated, RpgRuntimeContext ctx)
    {
        if (updated is null)
        {
            return;
        }

        var names = CollectPlistFieldNames(program, plistName);
        for (var index = 0; index < names.Count && index < updated.Count; index++)
        {
            if (updated[index] is not null)
            {
                ctx.WriteValue(names[index], updated[index]);
            }
        }
    }

    private static void BindParameters(RpgRuntimeContext ctx, RpgSubprocedure procedure, IReadOnlyList<RpgExpr> arguments)
    {
        for (var index = 0; index < procedure.Parameters.Count && index < arguments.Count; index++)
        {
            ctx.WriteValue(procedure.Parameters[index], arguments[index].Eval(ctx));
        }
    }

    private IReadOnlyList<object?> CollectParameters(RpgStatement stmt, RpgRuntimeContext ctx)
    {
        var plistName = stmt.Factor2;
        if (string.IsNullOrWhiteSpace(plistName))
        {
            return Array.Empty<object?>();
        }

        var parameters = new List<RpgExpr>();
        bool collecting = false;
        foreach (var candidate in ctx.Program.MainStatements)
        {
            if (candidate.Opcode == RpgOpcode.Plist &&
                string.Equals(candidate.Factor1, plistName, StringComparison.OrdinalIgnoreCase))
            {
                collecting = true;
                continue;
            }

            if (collecting)
            {
                if (candidate.Opcode != RpgOpcode.Parm)
                {
                    break;
                }

                parameters.Add(RpgExpressionParser.Parse(candidate.Factor1 ?? ""));
            }
        }

        return new ArgumentSnapshots(parameters, ctx);
    }

    private void CopyRecordToContext(RpgRuntimeContext ctx, IReadOnlyDictionary<string, object?> record)
    {
        foreach (var pair in record)
        {
            if (ctx.HasField(pair.Key))
            {
                ctx.WriteValue(pair.Key, pair.Value);
            }
        }
    }

    private static Dictionary<string, object?> BuildRecordValues(RpgRuntimeContext ctx,
        Ipc.Db.Definitions.RecordFormat format)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in format.Fields)
        {
            if (ctx.HasField(field.Name))
            {
                values[field.Name] = ctx.ReadValue(field.Name);
            }
        }

        return values;
    }

    private static string FirstFile(RpgStatement stmt) =>
        !string.IsNullOrWhiteSpace(stmt.Factor2) ? stmt.Factor2 :
        !string.IsNullOrWhiteSpace(stmt.Factor1) ? stmt.Factor1 :
        throw new RpgRuntimeException("A file name is required.");

    private static int SelfIndex(IReadOnlyList<RpgStatement> statements, RpgStatement statement)
    {
        for (var index = 0; index < statements.Count; index++)
        {
            if (ReferenceEquals(statements[index], statement))
            {
                return index;
            }
        }

        throw new RpgRuntimeException("Statement is not part of its containing block.");
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1]
            : value.Trim('\'');

    private static RpgExpr? ParseSafe(string? text)
    {
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : RpgExpressionParser.Parse(text);
        }
        catch (RpgException)
        {
            return null;
        }
    }

    private static void SetIndicators(string? operand, RpgRuntimeContext ctx, bool value)
    {
        if (string.IsNullOrWhiteSpace(operand))
        {
            return;
        }

        foreach (var token in operand.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = token.Trim();
            if (clean.Equals("*INLR", StringComparison.OrdinalIgnoreCase) ||
                clean.Equals("LR", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Indicators[0] = value;
                continue;
            }

            if (clean.StartsWith("*IN", StringComparison.OrdinalIgnoreCase))
            {
                clean = clean[3..];
            }

            if (int.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out var indicator))
            {
                if (indicator is >= 0 and <= 99)
                {
                    ctx.Indicators[indicator] = value;
                }
            }
        }
    }

    private static bool TryParseFor(string text, out string counter, out string start,
        out string end, out string? by)
    {
        counter = start = end = ""; by = null;
        var header = System.Text.RegularExpressions.Regex.Match(text, @"\A\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*");
        if (!header.Success) return false;
        counter = header.Groups[1].Value;
        var remainder = text[header.Length..];
        static int Keyword(string expression, string keyword)
        {
            var depth = 0; var quoted = false;
            for (var i = 0; i < expression.Length; i++)
            {
                var c = expression[i];
                if (c == '\'')
                {
                    if (quoted && i + 1 < expression.Length && expression[i + 1] == '\'') { i++; continue; }
                    quoted = !quoted; continue;
                }
                if (quoted) continue;
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (depth != 0 || i == 0 || !char.IsWhiteSpace(expression[i - 1]) ||
                    i + keyword.Length >= expression.Length || !char.IsWhiteSpace(expression[i + keyword.Length])) continue;
                if (expression.AsSpan(i, keyword.Length).Equals(keyword, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
        var to = Keyword(remainder, "TO"); if (to < 0) return false;
        start = remainder[..to].Trim(); remainder = remainder[(to + 2)..].Trim();
        var increment = Keyword(remainder, "BY");
        if (increment >= 0) { by = remainder[(increment + 2)..].Trim(); remainder = remainder[..increment].Trim(); }
        end = remainder;
        return start.Length > 0 && end.Length > 0 && (by is null || by.Length > 0);
    }

}

internal enum RpgLoopKind
{
    Do,
    DoU,
    DoW,
    For,
}

internal sealed class RpgLoopFrame
{
    public required int Start { get; init; }

    public required int End { get; init; }

    public required RpgLoopKind Kind { get; init; }

    public string? Counter { get; init; }

    public decimal EndValue { get; init; }

    public decimal Step { get; init; }

    public RpgExpr? Condition { get; init; }
}

internal sealed class RpgBlockState
{
    public Stack<RpgSelectFrame> Selects { get; } = new();
    public Stack<RpgLoopFrame> Loops { get; } = new();
}
internal sealed class RpgSelectFrame
{
    public required int Start { get; init; }
    public required int End { get; init; }
    public bool Matched { get; set; }
}
