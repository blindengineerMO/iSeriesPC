using Ipc.Rpg.Runtime;
using Ipc.Rpg.Model;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static string FilePrefix(string? argument, int line)
    {
        if (argument is null) return "";
        string value;
        if (argument.StartsWith('\''))
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(argument,@"\A'(?:[^']|'')*'\z"))
                throw new RpgCompileException(line,"PREFIX requires a name or character literal.");
            value = argument[1..^1].Replace("''","'",StringComparison.Ordinal);
            if (value != value.ToUpperInvariant()) throw new RpgCompileException(line,"A character literal PREFIX must be uppercase.");
        }
        else value = argument.ToUpperInvariant();
        if (value.Length > 64 || value.Length != 0 && !RpgWorkstationBindings.Name(value))
            throw new RpgCompileException(line,"This PREFIX requires a simple field prefix of at most 64 characters.");
        return value;
    }
}
