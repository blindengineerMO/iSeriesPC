using Ipc.Cl.Definitions;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;

namespace Ipc.Core.Tests;

public sealed class CommandDefinitionTests
{
    private const string Source = """
        /* Customer command */
        CMD PROMPT('Customer report')
        PARM KWD(CUSTOMER) TYPE(*NAME) LEN(10) MIN(1) PROMPT('Customer name' 2)
        PARM KWD(COUNT) TYPE(*DEC) LEN(5 0) DFT(10) RANGE(1 100) +
             PROMPT('Number of rows' 1)
        PARM KWD(MODE) TYPE(*CHAR) LEN(6) RSTD(*YES) VALUES(*FULL *SHORT) DFT(*SHORT)
        PARM KWD(TAGS) TYPE(*CHAR) LEN(12) MAX(3) CASE(*MIXED)
        PARM KWD(FILE) TYPE(FNAME) MIN(0) FILE(*IN)
        FNAME: QUAL TYPE(*NAME) LEN(10)
               QUAL TYPE(*NAME) LEN(10) DFT(*LIBL) SPCVAL((*LIBL) (*CURLIB))
        """;
    [Fact]
    public void Compile_and_bind_share_defaults_types_choices_lists_qualification_and_source_locations()
    {
        var definition = new CommandDefinitionCompiler().Compile(Source, "QGPL/REPORT", "QGPL/QCMDSRC(REPORT)");
        var bound = CommandBinder.Bind(definition, CommandParser.Parse("REPORT acme TAGS('Night run' daily) FILE(ORDERS)"));
        Assert.Equal("ACME", bound.Arguments[0]); Assert.Equal(10m, bound.Arguments[1]); Assert.Equal("*SHORT", bound.Arguments[2]);
        Assert.Equal(new object?[] { "Night run", "daily" }, Assert.IsAssignableFrom<IReadOnlyList<object?>>(bound.Arguments[3]));
        Assert.Equal("*LIBL/ORDERS", bound.Arguments[4]); Assert.Equal(4, definition.Parameters[1].SourceLine);
        Assert.Equal(1, definition.Parameters[1].PromptOrder); Assert.Equal(definition.ToJson(), CommandDefinition.FromJson(definition.ToJson()).ToJson());
        Assert.Equal(bound.Call.ToString(), CommandBinder.Bind(definition, bound.Call).Call.ToString());
    }
    [Fact]
    public void Positional_lists_and_keyword_lists_bind_the_same_metadata_and_JSON_duplicates_fail_closed()
    {
        var definition = new CommandDefinitionCompiler().Compile("CMD\nPARM KWD(N) TYPE(*INT2) MAX(3)", "QGPL/CPP");
        var positional = CommandBinder.Bind(definition, CommandParser.Parse("TEST (-2 255)"));
        var keywords = CommandBinder.Bind(definition, CommandParser.Parse("TEST N(-2 255)"));
        Assert.Equal(keywords.Call.ToString(), positional.Call.ToString());
        var buffer = Assert.IsType<Ipc.Core.Work.ProgramBuffer>(CommandArgumentCodec.ProgramArguments(definition, positional, 1208)[0]);
        Assert.Equal("0002FFFE00FF", Convert.ToHexString(buffer.ToArray()));
        var json = definition.ToJson();
        Assert.Throws<CpfException>(() => CommandDefinition.FromJson(json.Replace("\"Version\":1", "\"Version\":1,\"Version\":1")));
        Assert.Throws<CpfException>(() => CommandDefinition.FromJson(json.Replace("\"Keyword\":\"N\"", "\"Keyword\":\"N\",\"Keyword\":\"N\"")));
    }
    [Theory]
    [InlineData("REPORT")]
    [InlineData("REPORT A CUSTOMER(B)")]
    [InlineData("REPORT A COUNT(0)")]
    [InlineData("REPORT A COUNT(1.5)")]
    [InlineData("REPORT A MODE(*INVALID)")]
    [InlineData("REPORT A TAGS(a b c d)")]
    [InlineData("REPORT A UNKNOWN(1)")]
    [InlineData("REPORT A FILE(A/B/C)")]
    public void Invalid_invocations_are_rejected_before_a_processing_program_can_run(string command)
    {
        var definition = new CommandDefinitionCompiler().Compile(Source, "QGPL/REPORT");
        Assert.Throws<CpfException>(() => CommandBinder.Bind(definition, CommandParser.Parse(command)));
    }
    [Theory]
    [InlineData("CMD\nPARM KWD(A) TYPE(*INT4) EXTRA(*YES)", ":2:1:")]
    [InlineData("CMD\nPARM KWD(A) TYPE(*CHAR) +", ":2:1:")]
    [InlineData("CMD\nELEM TYPE(*CHAR)", ":2:1:")]
    [InlineData("CMD\nCMD", ":1:1:")]
    public void Unknown_or_malformed_definition_source_reports_its_original_location(string source, string location)
    {
        var error = Assert.Throws<ArgumentException>(() => new CommandDefinitionCompiler().Compile(source, "QGPL/CPP", "MEMBER"));
        Assert.Contains("MEMBER" + location, error.Message);
    }
    [Fact]
    public void Numeric_bounds_secret_errors_and_special_value_mappings_are_explicit()
    {
        var definition = new CommandDefinitionCompiler().Compile("CMD\nPARM KWD(A) TYPE(*UINT2) DFT(*MAX) SPCVAL((*MAX 65535))\nPARM KWD(B) TYPE(*NAME) SPCVAL((*ALL)) DFT(*ALL)", "QGPL/CPP");
        var bound = CommandBinder.Bind(definition, CommandParser.Parse("CMD")); Assert.Equal(65535m, bound.Arguments[0]); Assert.Equal("*ALL", bound.Arguments[1]);
        Assert.Throws<CpfException>(() => CommandBinder.Bind(definition, CommandParser.Parse("CMD A(65536)")));
        var secret = new CommandDefinitionCompiler().Compile("CMD\nPARM KWD(TOKEN) TYPE(*DEC) LEN(2) INLPMTLEN(*PWD)", "QGPL/CPP");
        var error = Assert.Throws<CpfException>(() => CommandBinder.Bind(secret, CommandParser.Parse("CMD TOKEN(private-value)"))); Assert.DoesNotContain("private-value", error.Message);
    }
    [Fact]
    public void CPP_byte_layouts_match_independent_count_binary_packed_and_EBCDIC_fixtures()
    {
        Assert.Equal("0002C14040C24040", Convert.ToHexString(CommandArgumentCodec.Encode(new() { Keyword = "LIST", Type = "*CHAR", Length = 3, Maximum = 3 }, new object?[] { "A", "B" }, 37)));
        Assert.Equal("FFFE", Convert.ToHexString(CommandArgumentCodec.Encode(new() { Keyword = "N", Type = "*INT2" }, -2m)));
        Assert.Equal("FFFFFFFF", Convert.ToHexString(CommandArgumentCodec.Encode(new() { Keyword = "N", Type = "*UINT4" }, 4294967295m)));
        Assert.Equal("00120D", Convert.ToHexString(CommandArgumentCodec.Encode(new() { Keyword = "N", Type = "*DEC", Length = 5, Decimals = 2 }, -1.2m)));
        var parameter = new ParameterDefinition { Keyword = "DATE", Type = "*DATE" };
        var date = Assert.Single(CommandBinder.Values(parameter, "'2024-02-29'")); Assert.Equal("1240229", date);
        Assert.Equal("F1F2F4F0F2F2F9", Convert.ToHexString(CommandArgumentCodec.Encode(parameter, date, 37)));
        Assert.Throws<CpfException>(() => CommandArgumentCodec.Encode(new() { Keyword = "TEXT", Type = "*CHAR", Length = 1 }, "é", 1208));
    }

}
