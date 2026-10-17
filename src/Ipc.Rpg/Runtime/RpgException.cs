namespace Ipc.Rpg.Runtime;

public class RpgException : Exception
{
    public RpgException(string? message) : base(message)
    {
    }
}

public sealed class RpgCompileException : RpgException
{
    public int LineNumber { get; }
    public Ipc.Core.Compilation.SourceLocation? Location { get; }
    public RpgCompileException(int lineNumber, string message)
        : base($"RPG{(lineNumber <= 0 ? string.Empty : $" line {lineNumber}")}: {message}")
    {
        LineNumber = lineNumber;
    }
    public RpgCompileException(Ipc.Core.Compilation.SourceLocation location, string message)
        : base($"RPG {location}: {message}") => Location = location;
}

public sealed class RpgCompileFailedException : RpgException
{
    public RpgCompileFailedException(string program, string library, int lineNumber, string message)
        : base($"{library}/{program}:{(lineNumber == 0 ? "?" : lineNumber.ToString())}: {message}")
    {
    }
}

public sealed class RpgRuntimeException : RpgException
{
    public Ipc.Core.Compilation.SourceLocation? Location { get; }
    public RpgRuntimeException(string? message) : base(message)
    {
    }
    public RpgRuntimeException(Ipc.Core.Compilation.SourceLocation location, string? message)
        : base($"RPG {location}: {message}") => Location = location;
}
