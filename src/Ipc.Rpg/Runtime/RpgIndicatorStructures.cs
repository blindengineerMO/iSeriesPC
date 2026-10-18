namespace Ipc.Rpg.Runtime;

public sealed partial class RpgRuntimeContext
{
    internal sealed record IndicatorTransfer(bool[] Values, byte[]? Bytes, int Offset);
    internal IndicatorTransfer WorkstationIndicators(string file)
    {
        var binding = _program.IndicatorBindings.SingleOrDefault(b => b.File == file);
        if (binding is null) return new(Indicators.ToArray(), null, 0);
        var bytes = _nativeStructures[binding.Structure]; var zero = NativeZero; var one = NativeOne;
        var offset = NativeOccurrenceOffset(_program.FindDs(binding.Structure)!);
        var result = new bool[100];
        for (var i = 0; i < 99; i++)
        {
            if (bytes[offset + i] != zero && bytes[offset + i] != one)
                throw new RpgRuntimeException("INDDS contains a non-indicator byte at position " + (i + 1) + ".") { StatusCode = 105 };
            result[i + 1] = bytes[offset + i] == one;
        }
        return new(result, bytes, offset);
    }
    internal Action PrepareWorkstationIndicators(IndicatorTransfer transfer, IReadOnlyDictionary<int, bool> indicators)
    {
        if (indicators.Any(p => p.Key is < 1 or > 99)) throw new RpgRuntimeException("Invalid workstation response indicator.");
        var changes = indicators.ToArray();
        if (transfer.Bytes is not { } bytes) return () => { foreach (var (index, value) in changes) Indicators[index] = value; };
        var zero = NativeZero; var one = NativeOne;
        return () => { foreach (var (index, value) in changes) bytes[transfer.Offset + index - 1] = value ? one : zero; };
    }
}
