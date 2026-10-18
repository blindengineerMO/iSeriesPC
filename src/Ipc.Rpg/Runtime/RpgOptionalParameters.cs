using Ipc.Rpg.Parsing;

namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    private int ReturnParameterOffset => _program.NativeReturnField?.ReturnByParameter == true ? 1 : 0;
    private readonly HashSet<string> _unpassedParameters = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _omittedParameters = new(StringComparer.OrdinalIgnoreCase);
    internal bool InSubprocedure { get; set; }
    internal void SetUnpassedParameters(IEnumerable<string> names) => _unpassedParameters.UnionWith(names);
    internal void BindOmittedParameter(string name)
    {
        if (!_program.HasNativeEntryInterface || _program.FindField(name)?.OmitParameter != true)
            throw NativeFailure(105, "An omitted argument requires OPTIONS(*OMIT) on the native PI parameter.");
        _omittedParameters.Add(name);
    }

    private void EnsureNativeParameterAvailable(NativeView view)
    {
        if (_program.GlobalStructures.Contains(view.Structure)) { GlobalContext!.EnsureNativeParameterAvailable(view); return; }
        if (!_nativeStructures.TryGetValue(view.Structure.Name, out var storage) || !ReferenceEquals(storage, view.Bytes)) return;
        var parameter = view.Structure.ParameterName ?? view.Element?.Name;
        if (parameter is not null && _unpassedParameters.Contains(parameter)) throw NativeFailure(105, "An unpassed OPTIONS(*NOPASS) parameter cannot be accessed: " + parameter);
        if (parameter is not null && _omittedParameters.Contains(parameter)) throw NativeFailure(105, "An omitted OPTIONS(*OMIT) parameter has no accessible storage: " + parameter);
    }

    private int ParameterNumber(IReadOnlyList<RpgExpr> arguments, string? presence = null)
    {
        if (InSubprocedure || !_program.HasNativeEntryInterface || arguments.Count != 1 || arguments[0] is not RpgFieldRef field)
            throw NativeFailure(105, "Parameter built-ins require one parameter name from the current native procedure interface.");
        var names = _program.NativeParameterLists["/*ENTRY"];
        for (var index = 0; index < names.Count; index++)
            if (names[index].Equals(field.Name, StringComparison.OrdinalIgnoreCase))
            {
                var parameter = _program.FindField(field.Name) ?? throw NativeFailure(105, "The interface parameter has no scalar declaration.");
                if (presence == "PASSED" && !parameter.NoPassParameter && !parameter.OmitParameter || presence == "OMITTED" && !parameter.OmitParameter)
                    throw NativeFailure(105, "%PASSED requires OPTIONS(*NOPASS or *OMIT); %OMITTED requires OPTIONS(*OMIT).");
                return index + 1 + ReturnParameterOffset;
            }
        throw NativeFailure(105, "The operand is not a parameter of the current native procedure interface.");
    }

    private bool ParameterPresence(IReadOnlyList<RpgExpr> arguments, bool omitted)
    {
        var number = ParameterNumber(arguments, omitted ? "OMITTED" : "PASSED");
        if (ParameterCount < number) return false;
        var hasOmission = _omittedParameters.Contains(_program.NativeParameterLists["/*ENTRY"][number - 1 - ReturnParameterOffset]);
        return omitted ? hasOmission : !hasOmission;
    }
}
