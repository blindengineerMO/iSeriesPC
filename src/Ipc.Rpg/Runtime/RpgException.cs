namespace Ipc.Rpg.Runtime;

public sealed class RpgException : Exception
{
    public RpgException(string? message) : base(message)
    {
    }
}

public sealed class RpgCompileException : Exception
{
    public RpgCompileException(int lineNumber, string message)
        : base($"RPG{(lineNumber <= 0 ? string.Empty : $" line {lineNumber}")}: {message}")
    {
    }
}

public sealed class RpgCompileFailedException : Exception
{
    public RpgCompileFailedException(string program, string library, int lineNumber, string message)
        : base($"{library}/{program}:{(lineNumber == 0 ? "?" : lineNumber.ToString())}: {message}")
    {
    }
}

public sealed class RpgRuntimeException : Exception
{
    public RpgRuntimeException(string? message) : base(message)
    {
    }
}