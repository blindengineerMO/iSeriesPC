namespace Ipc.Dsp;

/// <summary>A program-message lookup result. Keys are four opaque Latin-1 characters, preserving all four key bytes.</summary>
public sealed record DisplayMessage(string Text, string? HelpId = null);
