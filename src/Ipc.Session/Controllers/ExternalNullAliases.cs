using Ipc.Core.Messages;
using Ipc.Core.Work;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private static void RejectExternalNullAliases(IReadOnlyList<object?>? arguments)
    {
        if (arguments is null) return;
        var ranges = new Dictionary<object, List<(ProgramArgumentStorage Range, bool Map)>>(ReferenceEqualityComparer.Instance);
        foreach (var argument in arguments.OfType<ProgramArgument>())
        {
            if (argument.Storage is { } data) Add(data, false);
            if (argument.NullMap is not { } map) continue;
            for (var index = 0; index < map.Count; index++)
                if (map.Storage(index) is { } flag) Add(flag, true);
        }
        void Add(ProgramArgumentStorage range, bool map)
        {
            if (!ranges.TryGetValue(range.Owner, out var entries)) ranges.Add(range.Owner, entries = []);
            entries.Add((range, map));
        }
        foreach (var entries in ranges.Values)
        {
            var end = -1L; var mapEnd = -1L;
            foreach (var (range, map) in entries.OrderBy(entry => entry.Range.Offset))
            {
                if (range.Offset < (map ? end : mapEnd)) throw new CpfException("CPF3C3A", "External process calls require an alias-capable protocol for overlapping null maps or data and null indicators.");
                end = Math.Max(end, (long)range.Offset + range.Length);
                if (map) mapEnd = Math.Max(mapEnd, (long)range.Offset + range.Length);
            }
        }
    }
}
