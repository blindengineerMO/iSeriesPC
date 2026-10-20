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
        RpgProcedurePointerRegistry.Target? pointer = null;
        if (prototype?.PointerTarget is { } pointerName)
        {
            if (RpgExpressionParser.Parse(pointerName).Eval(caller) is not RpgProcedureAddress address) throw new RpgRuntimeException("EXTPROC requires a procedure pointer.") { StatusCode = 105 };
            pointer = caller.PointerRegistry.Resolve(address);
        }
        var local = prototype is { NativeProcedure: true } ? caller.Program.FindSubprocedure(prototype.ExternalName ?? prototype.Name) : null;
        var procedure = pointer?.Procedure ?? (prototype is { NativeProcedure: true } ? (local?.NativeBody is not null ? local : caller.Program.FindBoundProcedure(prototype.ExternalName ?? prototype.Name)) : null);
        RpgServiceProcedure? service = null;
        if (prototype is { NativeProcedure: true } && procedure is null && caller.Program.FindBoundService(prototype.ExternalName ?? prototype.Name) is { } import)
        {
            if (_host.ServiceProcedure is null) throw new RpgRuntimeException("No service-program host is available.") { StatusCode = 105 };
            try { service = _host.ServiceProcedure(import, _host); procedure = service.Procedure; }
            catch (InvalidDataException error) { throw new RpgRuntimeException(error.Message) { StatusCode = 202 }; }
        }
        if (prototype is null || procedure?.NativeBody is not { } body || expression && prototype.NativeReturnField is null)
            throw new RpgRuntimeException("The expression requires a bound native procedure: " + name) { StatusCode = 105 };
        if ((service is not null || pointer is not null) && !RpgCompiler.ProcedureCompatible(prototype, procedure))
            throw new RpgRuntimeException("Service procedure PI differs from its prototype: " + name) { StatusCode = 202, CallArgumentError = true };
        var pointerModule = pointer?.Context(_host);
        if (pointerModule is not null && pointerModule.NativeCcsid != _host.Ccsid())
            throw new RpgRuntimeException("Procedure address globals require their original CCSID.") { StatusCode = 452, CallArgumentError = true };
        using var values = PrepareCallArguments(() => new ArgumentSnapshots(arguments, caller, prototype));
        using var scope = PrepareCallArguments(() => new RpgArgumentScope(caller, arguments, values, _host.Ccsid(), prototype,
            capturedNative: values.NativeAt, preparedArguments: values.ArgumentAt));
        var bytes = body.DataStructures.Where(d => !d.IsTemplate && !d.StaticLocal && !body.GlobalStructures.Contains(d) && !d.Elements.Any(e => body.FindField(e.Name)?.StaticLocal == true)).Sum(d => (long)d.Length * d.StorageCount);
        if (body.NativeReturnField is { } resultLayout) bytes += RpgRuntimeContext.ParameterBytes(resultLayout);
        if (_nativeProcedureDepth >= 128 || bytes > 67108864 - _nativeProcedureBytes)
            throw new RpgRuntimeException("Native procedure invocations exceed 128 frames or 64 MiB of declared local storage.") { StatusCode = 202 };
        if (caller.ModuleContext.NativeCcsid != _host.Ccsid()) throw new RpgRuntimeException("Procedure globals require the module's original CCSID.") { StatusCode = 452, CallArgumentError = true };
        using var serviceScope = pointer?.Enter() ?? service?.Enter();
        var module = pointerModule ?? service?.ModuleContext() ?? caller.BoundModuleContext(body.GlobalProgram ?? caller.ModuleContext.Program, _host);
        _nativeProcedureDepth++; _nativeProcedureBytes += bytes;
        var savedContext = _context; var savedReturned = _returned; var savedHandling = _handlingError;
        var savedProcedure = _procedure; var savedInvocation = _procedureInvocation;
        RpgRuntimeContext? context = null;
        try
        {
            if (!ReferenceEquals(module, caller.ModuleContext))
            {
                _context = module;
                module.FileOpener = CreateDisk; module.WorkstationOpener = CreateWorkstation;
                var moduleInvocation = new object();
                module.FileErrorHandler = error => InvokeFileOwnerError(module, moduleInvocation, error);
                module.NativeFunction = (target, actuals) => InvokeNativeProcedure(module, target, actuals, expression: true);
                InitializeExternalFiles(module);
            }
            context = new(body, _host, module);
            context.NativeFunction = (target, actuals) => InvokeNativeProcedure(context, target, actuals, expression: true);
            context.BeginEntryBinding(); context.BeginFeedbackCall(arguments.Count + (body.NativeReturnField?.ReturnByParameter == true ? 1 : 0));
            BindEntryParameters(body, context, scope.Arguments); context.InitializeDeferredAddresses(); context.ProtectEntryParameters();
            _context = context; _returned = false; _handlingError = false; _procedure = null; _procedureInvocation = new object(); _depth++;
            context.FileOpener = CreateDisk;
            context.WorkstationOpener = CreateWorkstation;
            var fileInvocation = _procedureInvocation;
            context.FileErrorHandler = error => InvokeFileOwnerError(context, fileInvocation, error);
            try { Execute(body.MainStatements, context, new RpgBlockState(), procedure.Name, () => InitializeProcedureFiles(context)); }
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
            try { try { context?.CloseAutomaticFiles(); } finally { context?.ReleaseReferences(); } }
            finally
            {
                _context = savedContext; _returned = savedReturned; _handlingError = savedHandling;
                _procedure = savedProcedure; _procedureInvocation = savedInvocation;
                _nativeProcedureDepth--; _nativeProcedureBytes -= bytes;
            }
        }
    }
}
