using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgNativeStorageTests
{
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Writable_substrings_preserve_unselected_bytes_and_use_job_CCSID_for_text(int ccsid)
    {
        var program = RpgCompiler.Compile("QGPL", "SUBWRITE", """
            **free
            dcl-s RAW char(8) ccsid(*hex) inz(X'FF00FE0102030405');
            dcl-s TEXT char(6);
            eval %subst(RAW:3:2) = '10';
            eval %subst(RAW:5:3) = X'FF00';
            eval %subst(RAW:1:1) = %subst(RAW:5:2);
            eval %subst(TEXT:1) = '*BLANK';
            """);
        var context = new RpgInterpreter(new RpgHost { Ccsid = () => ccsid }).Run(program);
        var expected = new byte[] { 0xFF, 0x00 }.Concat(Ipc.Core.Text.CodePage.FromCcsid(ccsid).GetBytes("10")).Concat(new byte[] { 0xFF, 0x00, Ipc.Core.Text.CodePage.FromCcsid(ccsid).GetBytes(" ")[0], 0x05 }).ToArray();
        Assert.Equal(expected, Assert.IsType<ProgramBuffer>(context.ReadValue("RAW")).ToArray()); Assert.Equal("*BLANK", context.ReadValue("TEXT"));
    }
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    [InlineData(3, 1)]
    [InlineData(1, 3)]
    public void Invalid_writable_substring_ranges_leave_the_original_field_unchanged(int from, int length)
    {
        var program = RpgCompiler.Compile("QGPL", "SUBWRITE", "**free\ndcl-s RAW char(2) ccsid(*hex) inz(X'FF00');\neval %subst(RAW:" + from + ":" + length + ") = X'0000';");
        var host = new RpgHost(); var context = new RpgRuntimeContext(program, host);
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(host).Run(program, existingContext: context));
        Assert.Equal("FF00", Convert.ToHexString(Assert.IsType<ProgramBuffer>(context.ReadValue("RAW")).ToArray()));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void EVAL_copies_opaque_identifiers_without_decoding_unrequested_arithmetic_indicators(int ccsid)
    {
        const string hex = "FF00FE0180000000FF00FE0180000000";
        var program = RpgCompiler.Compile("QGPL", "COPYID", "**free\ndcl-s SOURCE char(16) ccsid(*hex) inz(X'" + hex + "');\ndcl-s TARGET char(16) ccsid(*hex);\neval TARGET = %subst(SOURCE:1:16);");
        var host = new RpgHost { Ccsid = () => ccsid }; var context = new RpgRuntimeContext(program, host); context.Indicators[1] = true;
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(hex, Convert.ToHexString(Assert.IsType<ProgramBuffer>(context.ReadValue("TARGET")).ToArray())); Assert.True(context.Indicators[1]);
    }
    [Theory]
    [InlineData(3, "80", "7F", -128L, 127L)]
    [InlineData(5, "8000", "7FFF", -32768L, 32767L)]
    [InlineData(10, "80000000", "7FFFFFFF", -2147483648L, 2147483647L)]
    [InlineData(20, "8000000000000000", "7FFFFFFFFFFFFFFF", long.MinValue, long.MaxValue)]
    public void Signed_integer_references_preserve_binary_width_aliases_range_and_call_lifetime(int digits, string minimum, string maximum, long min, long max)
    {
        var program = RpgCompiler.Compile("QGPL", "INTAPI", $"**free\ndcl-s N int({digits});\ncallp TEST(N:N);");
        ProgramArgument? retained = null;
        var host = new RpgHost { Ccsid = () => 1208, TypedProgramCaller = (_, _, _, borrow) => {
            var args = borrow!(); Assert.Same(args[0], args[1]); retained = Assert.IsType<ProgramArgument>(args[0]);
            Assert.Equal("*INT", retained.Type); Assert.Equal(minimum.Length / 2, retained.Length);
            retained.Value = new ProgramBuffer(Convert.FromHexString(minimum), 1208);
            Assert.Equal(min, retained.Value); Assert.Equal(minimum, Convert.ToHexString(retained.ToBuffer()!.ToArray()));
            retained.Value = (decimal)max; Assert.Equal(max, ((ProgramArgument)args[1]!).Value);
            Assert.Equal(maximum, Convert.ToHexString(retained.ToBuffer()!.ToArray()));
            Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => retained.Value = (decimal)max + 1).MessageId);
            Assert.Throws<CpfException>(() => retained.Value = (decimal)min - 1);
            Assert.Throws<CpfException>(() => retained.Value = 0.5m);
            Assert.Equal(max, retained.Value); return new() { Success = true };
        } };
        var context = new RpgInterpreter(host).Run(program); Assert.Equal(max, context.ReadValue("N"));
        Assert.Throws<ObjectDisposedException>(() => retained!.ToBuffer()); Assert.Throws<ObjectDisposedException>(() => retained!.Value = 0m);
    }

    [Fact]
    public void Integer_prototype_literals_are_typed_and_do_not_match_packed_actual_fields()
    {
        var program = RpgCompiler.Compile("QGPL", "INTAPI", "**free\ncallp TEST(-2147483648);");
        program.Fields.Add(new() { Name = "FORMAL", Kind = RpgFieldKind.Integer, Length = 10 });
        program.Prototypes.Add(new() { Name = "TEST", ExternalName = "FAKE", Parameters = { "FORMAL" } });
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            var constant = Assert.IsType<ProgramConstant>(Assert.Single(borrow!()));
            Assert.Equal("*INT", constant.Type); Assert.Equal(4, constant.Length); Assert.Equal("80000000", Convert.ToHexString(constant.Buffer.ToArray()));
            return new() { Success = true };
        } };
        new RpgInterpreter(host).Run(program);
        program.Fields.Add(new() { Name = "ACTUAL", Kind = RpgFieldKind.Packed, Length = 10 });
        program.MainStatements[0].Factor1 = "TEST(ACTUAL)";
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Hex_storage_and_substrings_preserve_every_byte_including_invalid_text(int ccsid)
    {
        var program = RpgCompiler.Compile("QGPL", "RAWAPI", "**free\ndcl-s RAW char(6) ccsid(*hex) inz(X'00FF80FE0041');\ncallp TEST(RAW:%subst(RAW:2:3):X'FF000080');");
        var host = new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) => {
            var args = borrow!(); var raw = Assert.IsType<ProgramArgument>(args[0]);
            Assert.Equal("00FF80FE0041", Convert.ToHexString(raw.ToBuffer()!.ToArray()));
            Assert.Equal("FF80FE", Convert.ToHexString(Assert.IsType<ProgramBuffer>(args[1]).ToArray()));
            Assert.Equal("FF000080", Convert.ToHexString(Assert.IsType<ProgramBuffer>(args[2]).ToArray()));
            raw.Value = new ProgramBuffer(Convert.FromHexString("FEFF80FF00FF"), ccsid); return new() { Success = true };
        } };
        var context = new RpgInterpreter(host).Run(program);
        Assert.Equal("FEFF80FF00FF", Convert.ToHexString(Assert.IsType<ProgramBuffer>(context.ReadValue("RAW")).ToArray()));
    }

    [Theory]
    [InlineData("char(2) ccsid(37)")]
    [InlineData("int(10) ccsid(*hex)")]
    [InlineData("char(2) ccsid(*hex) inz(X'00')")]
    [InlineData("char(2) ccsid(*hex) inz('AB')")]
    [InlineData("char(2) ccsid(*hex) ccsid(65535)")]
    public void Unsupported_or_inconsistent_raw_declarations_fail_during_compilation(string declaration)
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BADRAW", "**free\ndcl-s RAW " + declaration + ";"));

    [Fact]
    public void Raw_reference_arguments_cannot_bypass_the_aggregate_storage_limit()
    {
        var program = RpgCompiler.Compile("QGPL", "RAWAPI", "**free\ndcl-s RAW char(32767) ccsid(*hex);\ncallp TEST(" + string.Join(':', Enumerable.Repeat("RAW", 33)) + ");");
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => { _ = borrow!(); return new() { Success = true }; } };
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program)).MessageId);
    }
}
