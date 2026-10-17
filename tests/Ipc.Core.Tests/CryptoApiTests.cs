using System.Buffers.Binary;
using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed partial class CryptoApiTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    public CryptoApiTests() => _system.Start();
    private sealed class Cell
    {
        internal ProgramBuffer Buffer;
        internal ProgramArgument Argument;
        internal byte[] Bytes => Buffer.ToArray();
        internal Cell(int size = 96, int ccsid = 37, Func<ProgramBuffer, bool>? accept = null)
        {
            Buffer = new(Enumerable.Repeat((byte)0xFE, size).ToArray(), ccsid);
            Argument = new(() => Buffer, v => Buffer = (ProgramBuffer)v!, v => v is ProgramBuffer b && (accept?.Invoke(b) ?? true) ? b : throw new CpfException("CPF3C3A", "Rejected receiver."), "*CHAR", size, buffer: () => Buffer);
        }
    }
    private CryptoApi Api(int ccsid = 37, JobKey? job = null, CancellationToken cancellationToken = default) => new(_system.Connections, ccsid, job, cancellationToken);
    private static byte[] Hex(string text) => global::System.Convert.FromHexString(text);
    private static ProgramBuffer Raw(byte[] bytes, int ccsid = 37) => new(bytes, ccsid);
    private static ProgramBuffer Zero(int ccsid = 37) => new(new byte[4], ccsid);
    private static ProgramBuffer Integer(int value, int ccsid = 37)
    { var bytes = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(bytes, value); return new(bytes, ccsid); }
    private static ProgramBuffer Key(byte[] value, int type = 22, int ccsid = 37)
    {
        var bytes = new byte[12 + value.Length]; BinaryPrimitives.WriteInt32BigEndian(bytes, type); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), value.Length);
        bytes[8] = CodePage.FromCcsid(ccsid).GetBytes("0")[0]; value.CopyTo(bytes, 12); return new(bytes, ccsid);
    }
    private static ProgramBuffer Cipher(int mode = 1, int pad = 0, byte[]? iv = null, byte padCharacter = 0, int ccsid = 37)
    {
        var bytes = new byte[52]; BinaryPrimitives.WriteInt32BigEndian(bytes, 22); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 16);
        bytes[8] = CodePage.FromCcsid(ccsid).GetBytes(mode.ToString())[0]; bytes[9] = CodePage.FromCcsid(ccsid).GetBytes(pad.ToString())[0]; bytes[10] = padCharacter;
        (iv ?? new byte[16]).CopyTo(bytes, 20); return new(bytes, ccsid);
    }
    [Theory]
    [InlineData(1, "900150983CD24FB0D6963F7D28E17F72")]
    [InlineData(2, "A9993E364706816ABA3E25717850C26C9CD0D89D")]
    [InlineData(3, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD")]
    [InlineData(4, "CB00753F45A35E8BB5A03D699AC65007272C32AB0EDED1631A8B605A43FF5BED8086072BA1E7CC2358BAECA134C825A7")]
    [InlineData(5, "DDAF35A193617ABACC417349AE20413112E6FA4E89A97EA20A9EEEE64B55D39A2192992A274FC1A836BA3C23A3FEEBBD454D4423643CE80E2A9AC94FA54CA49F")]
    public void Published_hash_vectors_use_exact_data_bytes_and_preserve_receiver_tails(int algorithm, string expected)
    {
        foreach (var ccsid in new[] { 37, 1208 })
        {
            var output = new Cell(ccsid: ccsid); Api(ccsid).Invoke("QC3CALHA", [Raw(Hex("616263FFFF"), ccsid), 3, "DATA0100", Integer(algorithm, ccsid), "ALGD0500", "1", "", output.Argument, Zero(ccsid)]);
            Assert.Equal(expected, global::System.Convert.ToHexString(output.Bytes.AsSpan(0, expected.Length / 2))); Assert.All(output.Bytes[(expected.Length / 2)..], b => Assert.Equal(0xFE, b));
        }
    }
    [Theory]
    [InlineData(1, 16, "Hi There", "9294727A3638BB1C13F48EF8158BFC9D")]
    [InlineData(2, 20, "Hi There", "B617318655057264E28BC0B6FB378C8EF146BE00")]
    [InlineData(3, 131, "Test Using Larger Than Block-Size Key - Hash Key First", "60E431591EE0B67F0D8A26AACBF5B77F8E0BC6213728C5140546040F0EE37F54")]
    [InlineData(4, 131, "Test Using Larger Than Block-Size Key - Hash Key First", "4ECE084485813E9088D2C63A041BC5B44F9EF1012A2B588F3CD11F05033AC4C60C2EF6AB4030FE8296248DF163F44952")]
    [InlineData(5, 131, "Test Using Larger Than Block-Size Key - Hash Key First", "80B24263C7C1A3EBB71493C1DD7BE8B49B46D1F41B4AEEC1121B013783F8F3526B56D037E05F2598BD0FD2215D6A1E5295E64F73F63F0AEC8B915A985D786598")]
    public void RFC_2202_and_4231_HMAC_vectors_cover_short_and_long_hash_blocks(int algorithm, int keyLength, string data, string expected)
    {
        foreach (var ccsid in new[] { 37, 1208 })
        {
            var bytes = global::System.Text.Encoding.ASCII.GetBytes(data); var output = new Cell(ccsid: ccsid);
            Api(ccsid).Invoke("QC3CALHM", [Raw(bytes, ccsid), bytes.Length, "DATA0100", Integer(algorithm, ccsid), "ALGD0500", Key(Enumerable.Repeat((byte)(algorithm <= 2 ? 0x0B : 0xAA), keyLength).ToArray(), algorithm, ccsid), "KEYD0200", "0", "", output.Argument, Zero(ccsid)]);
            Assert.Equal(expected, global::System.Convert.ToHexString(output.Bytes.AsSpan(0, expected.Length / 2))); Assert.All(output.Bytes[(expected.Length / 2)..], b => Assert.Equal(0xFE, b));
        }
    }
    private const string Plain = "6BC1BEE22E409F96E93D7E117393172AAE2D8A571E03AC9C9EB76FAC45AF8E5130C81C46A35CE411E5FBC1191A0A52EFF69F2445DF4F9B17AD2B417BE66C3710";
    [Theory]
    [InlineData(0, "3AD77BB40D7A3660A89ECAF32466EF97F5D3D58503B9699DE785895A96FDBAAF43B1CD7F598ECE23881B00E3ED0306887B0C785E27E8AD3F8223207104725DD4")]
    [InlineData(1, "7649ABAC8119B246CEE98E9B12E9197D5086CB9B507219EE95DB113A917678B273BED6B8E3C1743B7116E69E222295163FF1CAA1681FAC09120ECA307586E1A7")]
    [InlineData(7, "874D6191B620E3261BEF6864990DB6CE9806F66B7970FDFF8617187BB9FFFDFF5AE4DF3EDBD5D35E5B4F09020DB03EAB1E031DDA2FBE03D1792170A0F3009CEE")]
    public void NIST_SP_800_38A_four_block_AES128_vectors_verify_both_directions_and_counter_carry(int mode, string expected)
    {
        foreach (var ccsid in new[] { 37, 1208 })
        {
            var key = Key(Hex("2B7E151628AED2A6ABF7158809CF4F3C"), ccsid: ccsid);
            var iv = mode == 0 ? new byte[16] : Hex(mode == 1 ? "000102030405060708090A0B0C0D0E0F" : "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
            var output = new Cell(ccsid: ccsid); var length = new Cell(8, ccsid);
            Api(ccsid).Invoke("QC3ENCDT", [Raw(Hex(Plain), ccsid), 64, "DATA0100", Cipher(mode, iv: iv, ccsid: ccsid), "ALGD0200", key, "KEYD0200", "1", "", output.Argument, 80, length.Argument, Zero(ccsid)]);
            Assert.Equal(expected, global::System.Convert.ToHexString(output.Bytes.AsSpan(0, 64))); Assert.Equal(64, BinaryPrimitives.ReadInt32BigEndian(length.Bytes));
            Assert.All(output.Bytes[64..], b => Assert.Equal(0xFE, b)); Assert.All(length.Bytes[4..], b => Assert.Equal(0xFE, b));
            var clear = new Cell(ccsid: ccsid);
            Api(ccsid).Invoke("QC3DECDT", [output.Buffer, 64, Cipher(mode, iv: iv, ccsid: ccsid), "ALGD0200", key, "KEYD0200", "1", "", clear.Argument, 80, length.Argument, Zero(ccsid)]);
            Assert.Equal(Plain, global::System.Convert.ToHexString(clear.Bytes.AsSpan(0, 64))); Assert.All(clear.Bytes[64..], b => Assert.Equal(0xFE, b));
        }
    }
    [Theory]
    [InlineData("8E73B0F7DA0E6452C810F32B809079E562F8EAD2522C6B7B", 0, "BD334F1D6E45F25FF712A214571FA5CC")]
    [InlineData("8E73B0F7DA0E6452C810F32B809079E562F8EAD2522C6B7B", 1, "4F021DB243BC633D7178183A9FA071E8")]
    [InlineData("8E73B0F7DA0E6452C810F32B809079E562F8EAD2522C6B7B", 7, "1ABC932417521CA24F2B0459FE7E6E0B")]
    [InlineData("603DEB1015CA71BE2B73AEF0857D77811F352C073B6108D72D9810A30914DFF4", 0, "F3EED1BDB5D2A03C064B5A7E3DB181F8")]
    [InlineData("603DEB1015CA71BE2B73AEF0857D77811F352C073B6108D72D9810A30914DFF4", 1, "F58C4C04D6E5F1BA779EABFB5F7BFBD6")]
    [InlineData("603DEB1015CA71BE2B73AEF0857D77811F352C073B6108D72D9810A30914DFF4", 7, "601EC313775789A5B7A7F504BBF3D228")]
    public void NIST_AES192_and_AES256_vectors_match_the_selected_mode(string key, int mode, string expected)
    {
        var iv = mode == 0 ? new byte[16] : Hex(mode == 1 ? "000102030405060708090A0B0C0D0E0F" : "F0F1F2F3F4F5F6F7F8F9FAFBFCFDFEFF");
        Assert.Equal(Hex(expected), _system.Cryptography.Transform(Hex(key), Hex(Plain)[..16], mode, 0, 0, iv, false));
        Assert.Equal(Hex(Plain)[..16], _system.Cryptography.Transform(Hex(key), Hex(expected), mode, 0, 0, iv, true));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(17)]
    public void Native_padding_modes_round_trip_and_CTR_accepts_partial_blocks(int count)
    {
        var key = new byte[16]; var data = Enumerable.Repeat((byte)0x80, count).ToArray();
        foreach (var pad in new[] { 1, 2 })
        {
            var encrypted = _system.Cryptography.Transform(key, data, 1, pad, 0xFE, new byte[16], false);
            Assert.Equal((count / 16 + 1) * 16, encrypted.Length);
            var padded = _system.Cryptography.Transform(key, encrypted, 1, 0, 0, new byte[16], true); var pads = encrypted.Length - count;
            Assert.All(padded.AsSpan(count, pads - 1).ToArray(), b => Assert.Equal(pad == 1 ? 0xFE : pads, b)); Assert.Equal(pads, padded[^1]);
            Assert.Equal(data, _system.Cryptography.Transform(key, encrypted, 1, 1, 0, new byte[16], true));
        }
        var counter = _system.Cryptography.Transform(key, data, 7, 0, 0, new byte[16], false);
        Assert.Equal(count, counter.Length); Assert.Equal(data, _system.Cryptography.Transform(key, counter, 7, 0, 0, new byte[16], true));
    }
    public void Dispose() => _system.Dispose();
}
