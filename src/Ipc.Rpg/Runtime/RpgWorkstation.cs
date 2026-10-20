using Ipc.Rpg.Model;

namespace Ipc.Rpg.Runtime;

public enum RpgWorkstationOperation { Write, Read, Exchange, Chain, ReadChanged, Update }
public sealed record RpgWorkstationResult(IReadOnlyDictionary<string, object?> Values, IReadOnlyDictionary<int, bool> Indicators,
    bool Found = true, bool EndOfFile = false, int? RelativeRecord = null);
public interface IRpgWorkstation : IDisposable
{
    RpgWorkstationResult Transfer(RpgWorkstationOperation operation, string record, IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<bool> indicators, int? relativeRecord = null, Action<RpgWorkstationResult>? validate = null);
}

internal sealed class RpgWorkstationState : IDisposable
{
    internal bool Initialized { get; set; }
    internal Dictionary<string, IRpgWorkstation> Files { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> Started { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, RpgRuntimeContext.FileStatus> Status { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, int> NumericStatus { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, byte[]> Feedback { get; } = new(StringComparer.OrdinalIgnoreCase);
    public void Dispose()
    {
        List<Exception>? errors = null;
        foreach (var file in Files.Values)
            try { file.Dispose(); } catch (Exception error) { (errors ??= new()).Add(error); }
        Files.Clear(); Started.Clear(); Initialized = false;
        if (errors is not null) throw new AggregateException("Could not close RPG workstations.", errors);
    }
}
