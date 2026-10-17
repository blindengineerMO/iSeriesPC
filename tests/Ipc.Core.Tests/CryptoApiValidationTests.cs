using System.Buffers.Binary;
using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class CryptoApiTests
{
    [Theory]
    [InlineData(0, 15)]
    [InlineData(22, 15)]
    [InlineData(22, 33)]
    [InlineData(3, 31)]
    [InlineData(4, 47)]
    [InlineData(5, 63)]
    public void Invalid_key_types_and_lengths_never_allocate_a_context(int type, int size)
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var output = new Cell();
        var failure = Assert.Throws<CpfException>(() => Api(job: job.Key).Invoke("QC3CRTKX", [Raw(new byte[size]), size, "0", type, "0", "", "", output.Argument, Zero()]));
        Assert.Equal(type == 0 ? "CPF9DE7" : "CPF9DDD", failure.MessageId); Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.Equal(0L, Count());
    }
    [Fact]
    public void Rejected_or_cancelled_context_output_rolls_back_creation_and_a_rejected_error_output_preserves_destroyed_state()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var output = new Cell(8, accept: _ => false);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api(job: job.Key).Invoke("QC3CRTKX", [Raw(new byte[16]), 16, "0", 22, "0", "", "", output.Argument, Zero()])).MessageId); Assert.Equal(0L, Count());
        using var stop = new CancellationTokenSource(); output = new Cell(8, accept: _ => { stop.Cancel(); return true; });
        Assert.ThrowsAny<OperationCanceledException>(() => Api(job: job.Key, cancellationToken: stop.Token).Invoke("QC3CRTKX", [Raw(new byte[16]), 16, "0", 22, "0", "", "", output.Argument, Zero()]));
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.Equal(0L, Count());
        var token = _system.Cryptography.CreateKeyContext(job.Key, 22, new byte[16]);
        var error = new Cell(16, accept: _ => false); var bytes = error.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); error.Buffer = new(bytes, 37);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api(job: job.Key).Invoke("QC3DESKX", [Raw(token), error.Argument])).MessageId); Assert.Equal(1L, Count());
        Api(job: job.Key).Invoke("QC3DESKX", [Raw(token), Zero()]); Assert.Equal(0L, Count());
    }
    [Theory]
    [InlineData(2, "DATA0200", "CPF9DD0")]
    [InlineData(4, "ALGD0100", "CPF9DD2")]
    [InlineData(6, "KEYD0400", "CPF9DD3")]
    [InlineData(7, "2", "CPF9DF0")]
    [InlineData(7, "9", "CPF9DEC")]
    [InlineData(8, "DEVICE", "CPF9DF8")]
    public void Unsupported_cipher_controls_leave_output_and_length_unchanged(int index, string value, string expected)
    {
        var output = new Cell(); var length = new Cell(4); var error = new Cell(16); var bytes = error.Bytes; BinaryPrimitives.WriteInt32BigEndian(bytes, 16); error.Buffer = new(bytes, 37);
        object?[] p = [Raw(new byte[16]), 16, "DATA0100", Cipher(), "ALGD0200", Key(new byte[16]), "KEYD0200", "1", "", output.Argument, 96, length.Argument, error.Argument];
        p[index] = value; Api().Invoke("QC3ENCDT", p); Assert.Equal(expected, new ProgramBuffer(error.Bytes.AsSpan(8, 7), 37).ToText());
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(length.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Fact]
    public void Short_buffers_aliases_and_length_normalization_fail_without_writing_data()
    {
        var output = new Cell(16); var length = new Cell(4, accept: _ => false);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Invoke("QC3ENCDT", [Raw(new byte[16]), 16, "DATA0100", Cipher(), "ALGD0200", Key(new byte[16]), "KEYD0200", "1", "", output.Argument, 16, length.Argument, Zero()])).MessageId);
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(length.Bytes, b => Assert.Equal(0xFE, b));
        Assert.Equal("CPF9DD6", Assert.Throws<CpfException>(() => Api().Invoke("QC3ENCDT", [Raw(new byte[16]), 16, "DATA0100", Cipher(pad: 2), "ALGD0200", Key(new byte[16]), "KEYD0200", "1", "", output.Argument, 16, new Cell(4).Argument, Zero()])).MessageId);
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => Api().Invoke("QC3ENCDT", [Raw(new byte[16]), 16, "DATA0100", Cipher(), "ALGD0200", Key(new byte[16]), "KEYD0200", "1", "", output.Argument, 16, output.Argument, Zero()])).MessageId);
    }
    [Fact]
    public void Raw_key_and_algorithm_reserved_fields_are_checked_but_unused_CBC_IV_tail_is_ignored()
    {
        var key = Key(new byte[16]).ToArray(); key[9] = 1; var output = new Cell(); var length = new Cell(4);
        object?[] p = [Raw(new byte[16]), 16, "DATA0100", Cipher(), "ALGD0200", Raw(key), "KEYD0200", "1", "", output.Argument, 96, length.Argument, Zero()];
        Assert.Equal("CPF9DEE", Assert.Throws<CpfException>(() => Api().Invoke("QC3ENCDT", p)).MessageId);
        p[5] = Key(new byte[16]); var description = Cipher().ToArray(); description[11] = 1; p[3] = Raw(description);
        Assert.Equal("CPF9DEE", Assert.Throws<CpfException>(() => Api().Invoke("QC3ENCDT", p)).MessageId);
        description[11] = 0; description[51] = 0xFF; p[3] = Raw(description); Api().Invoke("QC3ENCDT", p); Assert.Equal(16, BinaryPrimitives.ReadInt32BigEndian(length.Bytes));
    }
    [Fact]
    public void Invalid_decrypt_pad_count_produces_no_partial_clear_output()
    {
        var encrypted = _system.Cryptography.Transform(new byte[16], new byte[16], 1, 0, 0, new byte[16], false);
        var output = new Cell(); var length = new Cell(4);
        Assert.Equal("CPF9DC3", Assert.Throws<CpfException>(() => Api().Invoke("QC3DECDT", [Raw(encrypted), 16, Cipher(pad: 1), "ALGD0200", Key(new byte[16]), "KEYD0200", "1", "", output.Argument, 96, length.Argument, Zero()])).MessageId);
        Assert.All(output.Bytes, b => Assert.Equal(0xFE, b)); Assert.All(length.Bytes, b => Assert.Equal(0xFE, b));
    }
    [Theory]
    [InlineData("QC3CALHA", 8)]
    [InlineData("QC3CALHM", 10)]
    [InlineData("QC3ENCDT", 12)]
    [InlineData("QC3DECDT", 13)]
    [InlineData("QC3CRTKX", 10)]
    [InlineData("QC3DESKX", 1)]
    public void Wrong_counts_fail_before_parameter_storage_is_read(string name, int count)
        => Assert.Equal("CPF3C36", Assert.Throws<CpfException>(() => Api().Invoke(name, new object?[count])).MessageId);
    private long Count()
    { using var connection = _system.Connections.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT count(*) FROM sys_crypto_keys"; return (long)command.ExecuteScalar()!; }
    [Fact]
    public void Context_budget_is_per_job_and_completion_releases_every_slot()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var other = _system.Jobs.CreateInteractive("QUSER");
        var tokens = Enumerable.Range(0, 256).Select(_ => _system.Cryptography.CreateKeyContext(job.Key, 22, new byte[16])).ToArray();
        Assert.Equal(256, tokens.Select(global::System.Convert.ToHexString).Distinct().Count());
        Assert.Equal("IPC0126", Assert.Throws<CpfException>(() => _system.Cryptography.CreateKeyContext(job.Key, 22, new byte[16])).MessageId);
        _system.Cryptography.CreateKeyContext(other.Key, 22, new byte[16]); Assert.Equal(257, Count());
        _system.Jobs.Complete(job.Key); Assert.Equal(1, Count());
    }
    [Fact]
    public void Maximum_HMAC_key_context_is_usable_and_an_oversized_key_is_rejected_without_an_extra_row()
    {
        var job = _system.Jobs.CreateInteractive("QUSER"); var key = Enumerable.Repeat((byte)0xFF, 65536).ToArray();
        var token = _system.Cryptography.CreateKeyContext(job.Key, 5, key);
        Assert.Equal(global::System.Security.Cryptography.HMACSHA512.HashData(key, new byte[] { 0x80 }), _system.Cryptography.Hmac(job.Key, token, 5, [0x80]));
        Assert.Equal("CPF9DDD", Assert.Throws<CpfException>(() => _system.Cryptography.CreateKeyContext(job.Key, 5, new byte[65537])).MessageId); Assert.Equal(1, Count());
    }
}
