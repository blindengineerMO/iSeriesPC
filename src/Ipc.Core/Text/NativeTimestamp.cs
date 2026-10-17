using System.Buffers.Binary;

namespace Ipc.Core.Text;

/// <summary>IBM standard time: 52 microsecond bits and 12 uniqueness bits, centered on 2000.</summary>
public static class NativeTimestamp
{
    private const long HalfRange = 1L << 51;
    private const long Range = 1L << 52;
    private static readonly long CenterTicks = new DateTime(2000, 1, 1).Ticks;

    public static DateTime Decode(ReadOnlySpan<byte> bytes)
    {
        var micros = (long)(BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 12) - HalfRange;
        return new DateTime(CenterTicks + micros * 10, DateTimeKind.Unspecified);
    }

    /// <summary>Encode microseconds with zero uniqueness bits; optional wrapping uses the 52-bit time period.</summary>
    public static byte[] Encode(DateTime value, bool wrap = false)
    {
        var delta = value.Ticks - CenterTicks;
        var micros = delta / 10 - (delta < 0 && delta % 10 != 0 ? 1 : 0) + HalfRange;
        if (!wrap && (micros < 0 || micros >= Range)) throw new ArgumentOutOfRangeException(nameof(value));
        micros = ((micros % Range) + Range) % Range;
        var bytes = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(bytes, (ulong)micros << 12); return bytes;
    }
}
