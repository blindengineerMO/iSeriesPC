namespace Ipc.Core.Menu;

public sealed record WorkAction(string Option, string Text, string Command, bool Prompt = false, bool Confirm = false, IReadOnlyList<string>? Details = null);
public sealed record WorkRow(string Key, string Text, IReadOnlyList<WorkAction> Actions);
public sealed record WorkList(string Title, IReadOnlyList<WorkRow> Rows, string? RefreshCommand = null);
