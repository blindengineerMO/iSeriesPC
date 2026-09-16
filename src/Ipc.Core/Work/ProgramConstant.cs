namespace Ipc.Core.Work;

/// <summary>A private CALL temporary with a declared wire layout and semantic value
/// for legacy process protocol 1. Its bytes never depend on a callee declaration.</summary>
public sealed record ProgramConstant(string Type, int Length, int Decimals, ProgramBuffer Buffer, object Value);
