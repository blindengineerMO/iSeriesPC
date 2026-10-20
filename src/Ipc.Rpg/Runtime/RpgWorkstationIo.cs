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
            foreach (var file in context.Program.Workstations.Where(f => !f.Request.UserOpen && !f.Request.IsTemplate && !f.Request.IsParameter))
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
        var binding = context.Program.FindWorkstation(name);
        if (binding is null) return false;
        if (binding.Request.IsTemplate) throw FileFailure(105, "A TEMPLATE file has no runtime I/O.", name);
        var reference = context.FileReference(name); var owner = reference?.Owner ?? context; var actual = reference?.Workstation ?? binding;
        if (owner.WorkstationStateFor(actual.Request.Name).Files.ContainsKey(actual.Request.Name)) throw FileFailure(1215, "Workstation file is already open: " + name, name);
        owner.WorkstationStateFor(actual.Request.Name).Files.Add(actual.Request.Name, FileIo(name, () => reference is null ? CreateWorkstation(actual) : owner.WorkstationOpener!(actual), 1217));
        context.SetFileStatus(name, clearFileEof: true); return true;
    }
    private static bool CloseWorkstation(RpgStatement statement, RpgRuntimeContext context)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        if (context.Program.FindWorkstation(name) is not { } binding || binding.Request.Name != name) return false;
        var reference = context.FileReference(name); var owner = reference?.Owner ?? context; var actualName = reference?.Name ?? name;
        if (!owner.WorkstationStateFor(actualName).Files.Remove(actualName, out var file)) throw FileFailure(1211, "Workstation file is not open: " + name, name);
        file.Dispose(); return true;
    }
    private bool WorkstationIo(RpgStatement statement, RpgRuntimeContext context, RpgWorkstationOperation operation)
    {
        var name = FirstFile(statement).ToUpperInvariant();
        var binding = context.Program.FindWorkstation(name);
        if (binding is null) return false;
        if (binding.Request.IsTemplate) throw FileFailure(105, "A TEMPLATE file has no runtime I/O.", name);
        var format = binding.Layout.Records.SingleOrDefault(r => r.Name == name);
        if (statement.RecordDataStructure is not null && format is null && name == binding.Request.Name && binding.Layout.Records.Count == 1)
            format = binding.Layout.Records[0];
        var input = operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Chain or RpgWorkstationOperation.ReadChanged;
        var output = operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Update;
        var receiver = statement.RecordDataStructure is { } structure ? context.CaptureWorkstationStructure(structure, binding,
            format ?? throw new RpgRuntimeException("WORKSTN result input by file name requires one record format.") { StatusCode = 105 }, statement.Opcode, input) : null;
        var reference = context.FileReference(binding.Request.Name); var owner = reference?.Owner ?? context; var actual = reference?.Workstation ?? binding;
        var actualFormat = format is null ? null : actual.Layout.Records.Single(r => r.External == format.External);
        if (!owner.WorkstationStateFor(actual.Request.Name).Files.TryGetValue(actual.Request.Name, out var file)) throw FileFailure(1211, "Workstation file is closed: " + binding.Request.Name, binding.Request.Name);
        if (operation is RpgWorkstationOperation.Write or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Update && !binding.Request.Output ||
            operation is RpgWorkstationOperation.Read or RpgWorkstationOperation.Exchange or RpgWorkstationOperation.Chain or RpgWorkstationOperation.ReadChanged && !binding.Request.Input)
            throw new RpgRuntimeException("Operation is incompatible with workstation USAGE.");
        var subfile = actualFormat is null ? null : actual.Request.Subfiles.SingleOrDefault(s => RpgWorkstationBindings.RecordName(actual.Request, s.Record) == actualFormat.Name);
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
            var number = RpgValues.ToDecimal(owner.ReadValue(subfile.RelativeRecord));
            if (number != decimal.Truncate(number) || number is < 1 or > 9999) throw new RpgRuntimeException("SFILE RRN must be an integer from 1 to 9999.");
            rrn = (int)number;
        }
        var fields = receiver is not null
            ? output ? context.SendWorkstationRecord(receiver, format!) : new Dictionary<string, object?>()
            : (IReadOnlyDictionary<string, object?>)(format?.Fields ?? binding.Layout.Records.SelectMany(r => r.Fields).DistinctBy(f => f.Name).ToArray())
                .ToDictionary(f => f.Name, f => context.ReadFileValue(f.Name), StringComparer.Ordinal);
        var indicators = owner.WorkstationIndicators(actual.Request.Name);
        List<Action>? updates = null;
        Action? publishIndicators = null;
        Action? publishRecord = null;
        void Prepare(RpgWorkstationResult result)
        {
            updates = new();
            if (receiver is not null)
            { if (input && result.Values.Count > 0) publishRecord = context.PrepareWorkstationRecord(receiver, format!, result.Values); }
            else foreach (var (field, value) in result.Values)
            { var prepared = context.NormalizeWorkstationValue(field, value); updates.Add(() => context.WriteFileValue(field, prepared)); }
            if (result.RelativeRecord is { } number && subfile is not null)
            { var prepared = owner.NormalizeWorkstationValue(subfile.RelativeRecord, number); updates.Add(() => owner.WriteFileValue(subfile.RelativeRecord, prepared)); }
            publishIndicators = owner.PrepareWorkstationIndicators(indicators, result.Indicators);
        }
        try
        {
            var result = FileIo(binding.Request.Name, () => file.Transfer(operation, actualFormat?.Name ?? "", fields, indicators.Values, rrn, Prepare));
            if (updates is null) Prepare(result);
            publishRecord?.Invoke();
            foreach (var update in updates!) update();
            publishIndicators!();
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
