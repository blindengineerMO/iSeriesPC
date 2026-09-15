using Ipc.Cl.Commands;
using Ipc.Cl.Interpreter;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class ClByteFunctionTests
{
    [Theory]
    [InlineData("001C", "28")]
    [InlineData("FF1B", "-229")]
    [InlineData("8000", "-32768")]
    [InlineData("7FFF", "32767")]
    [InlineData("FFFFFFC7", "-57")]
    [InlineData("80000000", "-2147483648")]
    [InlineData("7FFFFFFF", "2147483647")]
    public void Binary_reads_match_independent_signed_big_endian_values(string bytes, string expected)
    {
        var input = new ProgramBuffer(Convert.FromHexString(bytes), 1208);
        var value = ClExpression.Compile("%BINARY(&RAW)").Evaluate(_ => input, 1208);
        Assert.Equal(decimal.Parse(expected, global::System.Globalization.CultureInfo.InvariantCulture), value);
    }
    [Theory]
    [InlineData(37, "00FF407FFFFF80000000")]
    [InlineData(1208, "00FF207FFFFF80000000")]
    public void Substring_and_binary_targets_preserve_neighbors_pad_and_truncate_fractional_values(int ccsid, string expected)
    {
        var program = new ClCompiler().Compile("BYTES", "QGPL", """
            PGM PARM(&RAW)
            DCL &RAW *CHAR LEN(10)
            DCL &POS *DEC LEN(3 0) VALUE(2)
            DCL &LEN *DEC LEN(3 0) VALUE(2)
            CHGVAR %SST(&RAW &POS &LEN) X'FF'
            CHGVAR VAR(%BIN(&RAW 4 2)) VALUE(32767.99)
            CHGVAR %SUBSTRING(&RAW 6 1) X'FF'
            CHGVAR %BINARY(&RAW 7 4) -2147483648
            ENDPGM
            """);
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => ccsid)
            .RunWithArguments(program, new object?[] { new ProgramBuffer(new byte[10], ccsid) });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal(expected, Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray()));
    }
    [Fact]
    public void Byte_ranges_can_split_UTF8_and_rhs_is_evaluated_before_overlapping_write()
    {
        var program = new ClCompiler().Compile("OVERLAP", "QGPL", """
            PGM PARM(&RAW)
            DCL &RAW *CHAR LEN(5)
            CHGVAR %SST(&RAW 2 4) %SST(&RAW 1 4)
            CHGVAR %SST(&RAW 1 1) 'é'
            ENDPGM
            """);
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208)
            .RunWithArguments(program, new object?[] { new ProgramBuffer(Convert.FromHexString("0102030405"), 1208) });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal("C301020304", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray()));
    }
    [Fact]
    public void Invalid_ranges_and_binary_overflow_are_monitorable_without_partial_assignment()
    {
        var program = new ClCompiler().Compile("BOUNDS", "QGPL", """
            PGM PARM(&RAW)
            DCL &RAW *CHAR LEN(2)
            CHGVAR %BIN(&RAW) 32768
            MONMSG MCH1210
            CHGVAR %SST(&RAW 2 2) X'FFFF'
            MONMSG IPC0006
            CHGVAR %SST(&RAW 0 1) X'FF'
            MONMSG IPC0006
            CHGVAR %SST(&RAW 1 0) X'FF'
            MONMSG IPC0006
            ENDPGM
            """);
        var result = new ClInterpreter((_, _) => null, _ => CommandResult.Ok(), ccsid: () => 1208)
            .RunWithArguments(program, new object?[] { new ProgramBuffer(Convert.FromHexString("AABB"), 1208) });
        Assert.False(result.Result.IsError, result.Result.Message);
        Assert.Equal("AABB", Convert.ToHexString(Assert.IsType<ProgramBuffer>(Assert.Single(result.Parameters)).ToArray()));
    }
    [Theory]
    [InlineData("%BIN(&RAW 1 3)")]
    [InlineData("%BIN(&RAW 1)")]
    [InlineData("%BIN(&RAW 1 &LEN)")]
    [InlineData("%SST(&RAW)")]
    [InlineData("%SST(X'FF' 1 1)")]
    [InlineData("%BIN(*LDA)")]
    [InlineData("%UNKNOWN(&RAW)")]
    public void Invalid_builtin_shapes_fail_compilation(string expression)
        => Assert.Throws<ClRuntimeException>(() => ClExpression.Compile(expression));

    [Fact]
    public void Byte_functions_require_declared_character_storage_and_are_bounded()
    {
        foreach (var type in new[] { "*DEC", "*LGL", "*INT" })
            Assert.Throws<ClCompileException>(() => new ClCompiler().Compile("TYPE", "QGPL", "DCL &N " + type + "\nCHGVAR %SST(&N 1 1) 'X'"));
        Assert.Throws<ClRuntimeException>(() => ClExpression.Compile(string.Concat(Enumerable.Repeat("%SST(&RAW ", 65)) + "1" + string.Concat(Enumerable.Repeat(" 1)", 65))));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Local_data_area_substrings_and_binary_fields_run_through_the_shared_job_host(int ccsid)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ByteFunctions22");
        ClExternalCallTests.Create(system, "LDAFUNC", "CLP", """
            PGM
            DCL &RAW *CHAR LEN(4) VALUE(X'FFFFFFC7')
            DCL &N *DEC LEN(10 0)
            CHGVAR %SST(*LDA 1021 4) &RAW
            CHGVAR &RAW %SUBSTRING(*LDA 1021 4)
            CHGVAR &N %BINARY(&RAW)
            IF (&N *EQ -57 *AND %SST(*LDA 1021 4) *EQ X'FFFFFFC7') THEN(SNDPGMMSG MSG('BYTE FUNCTIONS ACCEPTED'))
            ENDPGM
            """);
        using var session = new ExecutionSession(system, system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None);
        var result = session.Execute("CALL QGPL/LDAFUNC"); Assert.False(result.IsError, result.Message);
        Assert.Contains("BYTE FUNCTIONS ACCEPTED", result.Message);
        Assert.Equal("FFFFFFC7", Convert.ToHexString(new JobDataAreaStore(system.Connections).Read(session.Job.Key, JobDataArea.Local).AsSpan(1020, 4)));
    }
}
