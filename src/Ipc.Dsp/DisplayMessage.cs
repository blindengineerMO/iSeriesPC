namespace Ipc.Dsp;

/// <summary>A program-message lookup result. Keys are four opaque Latin-1 characters, preserving all four key bytes.</summary>
public sealed record DisplayMessage(string Text, string? HelpId = null, MessageHelp? Details = null);

public sealed record MessageHelp(string Title, string Text, string SecondLevel);
public sealed record SubfileMessage(string Key, DisplayMessage Message);

public interface IProgramMessageSource
{
    DisplayMessage? Read(string queue, string key);
    IReadOnlyList<SubfileMessage> ReadAll(string queue);
}
