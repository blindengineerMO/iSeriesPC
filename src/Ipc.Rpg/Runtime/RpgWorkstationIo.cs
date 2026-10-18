using Ipc.Core.Messages;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgInterpreter
{
    private IRpgWorkstation CreateWorkstation(RpgWorkstationBinding binding)
        => _host.OpenWorkstation?.Invoke(binding) ?? throw new RpgRuntimeException("WORKSTN execution requires a workstation host.");
    private void InitializeWorkstations(RpgRuntimeContext context)
    {
        var state = context.WorkstationState;
        if (state.Initialized)
        {
            foreach (var file in state.Files.Values) _host.RebindWorkstation?.Invoke(file);
            return;
        }
        try
        {
            foreach (var file in context.Program.Workstations.Where(f => !f.Request.UserOpen))
            {
                state.Files.Add(file.Request.Name, ImplicitFile(file.Request.Name, () => CreateWorkstation(file)));
                context.SetFileStatus(file.Request.Name, clearFileEof: true);
            }
            state.Initialized = true;
        }
        catch { context.CloseWorkstations(); throw; }
    }
    private bool OpenWorkstation(RpgStatement statement, RpgRuntimeContext context)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.Workstations.SingleOrDefault(f => f.Request.Name == name);
        if (binding is null) return false;
        if (context.WorkstationState.Files.ContainsKey(name)) throw FileFailure(1215, "Workstation file is already open: " + name, name);
        context.WorkstationState.Files.Add(name, CreateWorkstation(binding)); context.SetFileStatus(name, clearFileEof: true); return true;
    }
    private static bool CloseWorkstation(RpgStatement statement, RpgRuntimeContext context)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        if (!context.Program.Workstations.Any(f => f.Request.Name == name)) return false;
        if (!context.WorkstationState.Files.Remove(name, out var file)) throw FileFailure(1211, "Workstation file is not open: " + name, name);
        file.Dispose(); return true;
    }
    private bool WorkstationIo(RpgStatement statement, RpgRuntimeContext context, RpgWorkstationOperation operation)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.Workstations.SingleOrDefault(f => f.Request.Name == name || f.Layout.Records.Any(r => r.Name == name));
        if (binding is null) return false;
        var format = binding.Layout.Records.SingleOrDefault(r => r.Name == name);
        if (!context.WorkstationState.Files.TryGetValue(binding.Request.Name, out var file)) throw FileFailure(1211, "Workstation file is closed: " + binding.Request.Name, binding.Request.Name);
        if (operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Update && !binding.Request.Output ||
            operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Chain or RpgWorkstationOperation.ReadChanged && !binding.Request.Input)
            throw new RpgRuntimeException("Operation is incompatible with workstation USAGE.");
        var subfile = format is null ? null : binding.Request.Subfiles.SingleOrDefault(s => s.Record == format.Name);
        int? rrn = null;
        if (operation == RpgWorkstationOperation.Chain)
        {
            var key = Eval(statement.Factor1, context);
            var number = RpgValues.ToDecimal(key);
            if (number != decimal.Truncate(number) || number is < 1 or > 9999) throw new RpgRuntimeException("CHAIN requires a relative record number from 1 to 9999.");
            rrn = (int)number;
        }
        else if (subfile is not null && operation == RpgWorkstationOperation.Write)
        {
            var number = RpgValues.ToDecimal(context.ReadValue(subfile.RelativeRecord));
            if (number != decimal.Truncate(number) || number is < 1 or > 9999) throw new RpgRuntimeException("SFILE RRN must be an integer from 1 to 9999.");
            rrn = (int)number;
        }
        var fields = (format?.Fields ?? binding.Layout.Records.SelectMany(r => r.Fields).DistinctBy(f => f.Name).ToArray())
            .ToDictionary(f => f.Name, f => context.ReadValue(f.Name), StringComparer.Ordinal);
        List<(string Name, object? Value)>? updates = null;
        void Prepare(RpgWorkstationResult result)
        {
            updates = new();
            foreach (var (field, value) in result.Values) updates.Add((field, context.NormalizeWorkstationValue(field, value)));
            if (result.RelativeRecord is { } number && subfile is not null)
                updates.Add((subfile.RelativeRecord, context.NormalizeWorkstationValue(subfile.RelativeRecord, number)));
            if (result.Indicators.Any(p => p.Key is < 1 or > 99)) throw new RpgRuntimeException("Invalid workstation response indicator.");
        }
        try
        {
            var result = FileIo(binding.Request.Name, () => file.Transfer(operation, format?.Name ?? "", fields, context.Indicators, rrn, Prepare));
            if (updates is null) Prepare(result);
            foreach (var update in updates!) context.WriteValue(update.Name, update.Value);
            foreach (var indicator in result.Indicators) context.Indicators[indicator.Key] = indicator.Value;
            context.SetFileStatus(binding.Request.Name, operation == RpgWorkstationOperation.Chain ? result.Found : null,
                operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.ReadChanged || operation == RpgWorkstationOperation.Write && format?.Subfile == true ? result.EndOfFile : null,
                clearFileEof: operation == RpgWorkstationOperation.Chain && result.Found);
            context.RecordStatus(0, binding.Request.Name);
            if (operation == RpgWorkstationOperation.Chain && !result.Found) context.RecordStatus(12, binding.Request.Name);
            if (result.EndOfFile && operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.ReadChanged) context.RecordStatus(11, binding.Request.Name);
            if (result.EndOfFile && operation == RpgWorkstationOperation.Write && format?.Subfile == true) context.RecordStatus(13, binding.Request.Name);
            if (statement.Indicator2 != 0) context.Indicators[statement.Indicator2] = false;
            if (operation == RpgWorkstationOperation.Chain && statement.Indicator1 != 0) context.Indicators[statement.Indicator1] = !result.Found;
            if (operation == RpgWorkstationOperation.ReadChanged && statement.Indicator3 != 0) context.Indicators[statement.Indicator3] = result.EndOfFile;
            return true;
        }
        catch (Exception error) when (error is CpfException { MessageId: not "CPF1241" } or RpgRuntimeException)
        {
            context.SetFileStatus(binding.Request.Name, error: true);
            if (error is RpgRuntimeException { FileError: true } or RpgRuntimeException { StatusCode: not null }) throw;
            throw FileFailure(1299, error.Message, binding.Request.Name, error);
        }
    }
}
