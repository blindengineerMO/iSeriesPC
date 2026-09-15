namespace Ipc.Core.Work;

/// <summary>Identity of an exception already delivered to a program call queue.</summary>
public sealed record ProgramMessageReference(string Queue, uint Key);
