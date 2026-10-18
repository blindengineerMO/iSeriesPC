using Ipc.Core.Messages;
using Ipc.Core.Work;

namespace Ipc.Core.Tests;

public sealed class ProgramArgumentMemoryTests
{
    [Theory]
    [InlineData(-1, 1)] [InlineData(0, -1)] [InlineData(4, 1)] [InlineData(3, 2)]
    public void Invalid_ranges_never_reach_the_memory_provider(int offset, int length)
    {
        var reads = 0; var memory = new ProgramArgumentMemory(new(new object(), 0, 4), 37, null,
            (_, count) => { reads++; return new(new byte[count], 37); }, (_, _) => { }, (_, _) => { });
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => memory.Read(offset, length)).MessageId); Assert.Equal(0, reads);
    }

    [Fact]
    public void Slices_preserve_identity_and_validate_before_the_write_callback()
    {
        var owner = new object(); var bytes = new byte[] { 1, 2, 3, 4 }; var writes = 0;
        var memory = new ProgramArgumentMemory(new(owner, 10, 4), 37, null,
            (offset, count) => new(bytes.AsSpan(offset, count), 37), (_, value) => { if (value.ToArray().Contains((byte)9)) throw new CpfException("CPF3C3A", "Rejected"); },
            (offset, value) => { writes++; value.ToArray().CopyTo(bytes, offset); });
        var slice = memory.Slice(1, 2); Assert.Same(owner, slice.Storage.Owner); Assert.Equal(11, slice.Storage.Offset); Assert.Equal(2, slice.Capacity);
        Assert.Throws<CpfException>(() => slice.Write(0, new(new byte[] { 9 }, 37))); Assert.Equal(0, writes);
        slice.Write(0, new(new byte[] { 8 }, 37)); Assert.Equal(new byte[] { 1, 8, 3, 4 }, bytes); Assert.Equal(1, writes);
    }

    [Fact]
    public void Readonly_scoped_memory_and_its_slices_expire_before_any_access()
    {
        var active = true;
        var memory = new ProgramArgumentMemory(new(new object(), 0, 4), 37, null, (_, count) => new(new byte[count], 37), (_, _) => { }, (_, _) => { });
        var scoped = memory.Scoped(() => ObjectDisposedException.ThrowIf(!active, memory), readOnly: true).Slice(1, 2);
        Assert.Equal("CPF3C90", Assert.Throws<CpfException>(() => scoped.Write(0, new(new byte[] { 1 }, 37))).MessageId);
        active = false; Assert.Throws<ObjectDisposedException>(() => scoped.Read(0, 1)); Assert.Throws<ObjectDisposedException>(() => scoped.Validate(0, new(new byte[] { 1 }, 37)));
    }
}
