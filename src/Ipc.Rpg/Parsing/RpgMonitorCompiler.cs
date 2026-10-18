using System.Globalization;
using Ipc.Rpg.Model;
using Ipc.Rpg.Runtime;

namespace Ipc.Rpg.Parsing;

internal static class RpgMonitorCompiler
{
    private sealed class Group(int start)
    {
        internal int Start { get; } = start;
        internal bool Handler { get; set; }
        internal List<int> Handlers { get; } = [];
    }
    private static readonly HashSet<int> Codes = [100, 102, 103, 105, 112, 121, 123, 202, 452, 907, 1021, 1022, 1211, 1215, 1216, 1217, 1218, 1221, 1222, 1299, 9999];
    internal static void Bind(List<RpgStatement> statements)
    {
        var groups = new Stack<Group>();
        var handlerRegions = new Dictionary<RpgStatement, int>();
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            if (statement.Opcode == RpgOpcode.OnError && groups.TryPeek(out var handlerGroup)) handlerGroup.Handler = true;
            statement.Monitor = groups.FirstOrDefault(g => !g.Handler)?.Start ?? -1;
            handlerRegions[statement] = groups.FirstOrDefault(g => g.Handler)?.Start ?? -1;
            switch (statement.Opcode)
            {
                case RpgOpcode.Monitor: groups.Push(new(index)); break;
                case RpgOpcode.OnError when groups.TryPeek(out var group):
                    if (group.Handlers.Count >= 256) throw Error(statement, "MONITOR exceeds 256 handlers.");
                    group.Handlers.Add(index); statement.ErrorStatuses = Parse(statement); break;
                case RpgOpcode.EndMon:
                    var closed = groups.Pop(); statements[closed.Start].End = index;
                    statements[closed.Start].ErrorHandlers = closed.Handlers.ToArray();
                    foreach (var handler in closed.Handlers) statements[handler].End = index;
                    break;
                case RpgOpcode.Goto when groups.TryPeek(out var enclosing) && !enclosing.Handler:
                    throw Error(statement, "GOTO is unsupported inside a MONITOR body; use LEAVE, ITER or RETURN.");
            }
        }
        foreach (var statement in statements.Where(s => s.Opcode == RpgOpcode.Goto))
        {
            var label = statements.FirstOrDefault(s => s.Opcode == RpgOpcode.Tag && string.Equals(s.Factor1, statement.Factor1, StringComparison.OrdinalIgnoreCase));
            if (label is not null && (label.Monitor >= 0 && label.Monitor != statement.Monitor ||
                handlerRegions[label] >= 0 && handlerRegions[label] != handlerRegions[statement]))
                throw Error(statement, "GOTO cannot enter a MONITOR body or handler.");
        }
    }
    private static int[] Parse(RpgStatement statement)
    {
        var parts = (statement.Factor1 ?? "").Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && parts[0].Length == 0) return [-1];
        if (parts.Length > 32) throw Error(statement, "ON-ERROR supports at most 32 status filters.");
        return parts.Select(part => part.ToUpperInvariant() switch
        {
            "*ALL" => -1, "*PROGRAM" => -2, "*FILE" => -3,
            _ => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var code) && Codes.Contains(code)
                ? code : throw Error(statement, "Unsupported ON-ERROR status; use a documented status or *ALL/*PROGRAM/*FILE.")
        }).ToArray();
    }
    private static RpgCompileException Error(RpgStatement statement, string message) => statement.Location is { } location
        ? new(location, message) : new(statement.LineNumber, message);
}
