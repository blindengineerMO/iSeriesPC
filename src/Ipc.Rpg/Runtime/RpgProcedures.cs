using Ipc.Core.Messages;
using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private int _nativeProcedureDepth;
    private long _nativeProcedureBytes;
    private object? InvokeNativeProcedure(RpgRuntimeContext caller, string name, IReadOnlyList<RpgExpr> arguments, bool expression)
    {
        var prototype = caller.Program.FindPrototype(name);
        var procedure = prototype is { NativeProcedure: true } ? caller.Program.FindSubprocedure(prototype.ExternalName ?? prototype.Name) : null;
        if (prototype is null || procedure?.NativeBody is not { } body || expression && prototype.NativeReturnField is null)
            throw new RpgRuntimeException("The expression requires a locally bound value-returning procedure: " + name) { StatusCode = 105 };
        using var values = PrepareCallArguments(() => new ArgumentSnapshots(arguments, caller, prototype));
        using var scope = PrepareCallArguments(() => new RpgArgumentScope(caller, arguments, values, _host.Ccsid(), prototype,
            capturedNative: values.NativeAt, preparedArguments: values.ArgumentAt));
        var bytes = body.DataStructures.Where(d => !d.IsTemplate && !body.GlobalStructures.Contains(d)).Sum(d => (long)d.Length * d.StorageCount);
        if (body.NativeReturnField is { } resultLayout) bytes += RpgRuntimeContext.ParameterBytes(resultLayout);
        if (_nativeProcedureDepth >= 128 || bytes > 67108864 - _nativeProcedureBytes)
            throw new RpgRuntimeException("Native procedure invocations exceed 128 frames or 64 MiB of declared local storage.") { StatusCode = 202 };
        if (caller.ModuleContext.NativeCcsid != _host.Ccsid()) throw new RpgRuntimeException("Procedure globals require the module's original CCSID.") { StatusCode = 452, CallArgumentError = true };
        _nativeProcedureDepth++; _nativeProcedureBytes += bytes;
        var savedContext = _context; var savedReturned = _returned; var savedHandling = _handlingError;
        var savedProcedure = _procedure; var savedInvocation = _procedureInvocation;
        RpgRuntimeContext? context = null;
        try
        {
            context = new(body, _host, caller.ModuleContext);
            context.NativeFunction = (target, actuals) => InvokeNativeProcedure(context, target, actuals, expression: true);
            context.BeginEntryBinding(); context.BeginFeedbackCall(arguments.Count + (body.NativeReturnField?.ReturnByParameter == true ? 1 : 0));
            BindEntryParameters(body, context, scope.Arguments); context.ProtectEntryParameters();
            _context = context; _returned = false; _handlingError = false; _procedure = null; _procedureInvocation = new object(); _depth++;
            try { Execute(body.MainStatements, context, new RpgBlockState(), procedure.Name); }
            finally { _depth--; }
            if (body.NativeReturnField is not null && !context.HasProcedureReturn)
                throw new RpgRuntimeException("A value-returning procedure reached its end without RETURN.") { StatusCode = 202 };
            return context.ProcedureReturn;
        }
        catch (RpgRuntimeException error)
        { throw new RpgRuntimeException(error.Message) { StatusCode = 202, ServiceError = error.ServiceError }; }
        catch (CpfException error) when (error.MessageId == "RNX9001" && context?.IsCancelled == true)
        { caller.ModuleContext.IsCancelled = true; throw; }
        catch (CpfException error) when (error.MessageId != "CPF1241")
        { throw new RpgRuntimeException(error.Message) { StatusCode = 202, ServiceError = error }; }
        finally
        {
            try { context?.ReleaseReferences(); }
            finally
            {
                _context = savedContext; _returned = savedReturned; _handlingError = savedHandling;
                _procedure = savedProcedure; _procedureInvocation = savedInvocation;
                _nativeProcedureDepth--; _nativeProcedureBytes -= bytes;
            }
        }
    }
}
