using Ipc.Core.Compilation;
using Ipc.Core.Objects;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

public static partial class RpgCompiler
{
    private static IReadOnlyList<string> ParseBindingDirectoryControl(string? text, int line)
    {
        if (text is null) throw new RpgCompileException(line, "BNDDIR requires quoted directory names.");
        var values = DeclarationArguments(text, line);
        if (values.Length is < 1 or > 16) throw new RpgCompileException(line, "BNDDIR accepts 1 through 16 quoted names.");
        return values.Select(value =>
        {
            if (value.Length < 3 || value[0] != '\'' || value[^1] != '\'') throw new RpgCompileException(line, "BNDDIR requires quoted names separated by colons.");
            var name = value[1..^1].ToUpperInvariant(); var parts = name.Split('/');
            if (parts.Length is < 1 or > 2 || !ObjectName.IsValid(parts[^1]) || parts.Length == 2 && parts[0] is not ("*LIBL" or "*CURLIB") && !ObjectName.IsValid(parts[0]))
                throw new RpgCompileException(line, "Invalid binding-directory name.");
            return name;
        }).ToArray();
    }
    /// <summary>Discover controls before deciding whether CRTBNDRPG needs a module binding pass.</summary>
    public static IReadOnlyList<string> SourceBindingDirectories(PreprocessedSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            var program = new RpgProgram { Name = "CONTROL", Library = "QGPL", Source = source, IsModule = true };
            var lines = ReadSpecs(ExtractCompileTimeData(program, source, cancellationToken));
            var free = lines.Any(l => l.TrimStart().StartsWith("**free", StringComparison.OrdinalIgnoreCase));
            _ = ExtractNativeProcedures(lines, free, cancellationToken);
            ParseNullControl(lines, program, free, cancellationToken); return program.BindingDirectories;
        }
        catch (RpgCompileException error) when (error.Location is null && error.LineNumber > 0 && error.LineNumber <= source.Locations.Count)
        { throw new RpgCompileException(source.Locations[error.LineNumber - 1], error.Message); }
    }
}
