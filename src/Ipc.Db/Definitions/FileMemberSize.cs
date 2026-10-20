using Ipc.Core.Messages;
using System.Text.Json.Serialization;

namespace Ipc.Db.Definitions;

public sealed record FileMemberSize(int InitialRecords = 10000, int IncrementRecords = 1000, int MaximumIncrements = 3, bool Unlimited = false)
{
    [JsonIgnore]
    public long Capacity => Unlimited ? 4294967288L : (long)InitialRecords + (long)IncrementRecords * MaximumIncrements;
    public void Validate()
    {
        if (InitialRecords is < 1 or > 2147483646 || IncrementRecords is < 0 or > 32767 || MaximumIncrements is < 0 or > 32767 ||
            (IncrementRecords == 0) != (MaximumIncrements == 0)) throw new CpfException("IPC0138", "Invalid physical member SIZE values.");
    }
    public long NextExtent(long current, long? effectiveCapacity = null)
    {
        Validate();
        if (Unlimited) throw new CpfException("IPC0138", "TOTRCDS(*NXTINCR) is not valid for SIZE(*NOMAX).");
        var capacity=effectiveCapacity??Capacity;
        if (current < InitialRecords) return InitialRecords;
        if (IncrementRecords == 0 || current >= capacity) throw new CpfException("CPF5030", "Member SIZE has no further automatic increment.");
        return Math.Min(capacity, InitialRecords + ((current - InitialRecords) / IncrementRecords + 1) * IncrementRecords);
    }
}

public sealed record FileMemberSizeChange(int? InitialRecords = null, int? IncrementRecords = null, int? MaximumIncrements = null, bool? Unlimited = null)
{
    public FileMemberSize Apply(FileMemberSize? previous)
    {
        previous ??= new();
        var result = new FileMemberSize(InitialRecords ?? previous.InitialRecords, IncrementRecords ?? previous.IncrementRecords,
            MaximumIncrements ?? previous.MaximumIncrements, Unlimited ?? previous.Unlimited);
        result.Validate(); return result;
    }
}
