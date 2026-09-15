using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Xunit;

namespace Ipc.Core.Tests;

public class CommandContractTests
{
    [Theory]
    [InlineData("CRTLIB LIB(TEST)", "IPC0002")]
    [InlineData("WRKCLASS", "IPC0004")]
    [InlineData("DOESNOTEXIST", "IPC0001")]
    [InlineData("GO MENU(MAIN) MENU(OTHER)", "IPC0005")]
    [InlineData("GO MENU('oops)", "IPC0005")]
    public void Unavailable_and_invalid_commands_have_distinct_diagnostics(string line, string code)
    {
        var result = new CommandCatalog(builtinContracts: true).Execute(line);
        Assert.True(result.IsError);
        Assert.StartsWith(code, result.Message);
    }

    [Theory]
    [InlineData("CPYF FROMFILE(A) TOFILE(B) FROMKEY(1 10)")]
    [InlineData("CPYF A B")]
    public void Unsupported_parameters_never_reach_the_handler(string line)
    {
        var invoked = false;
        var catalog = new CommandCatalog(builtinContracts: true);
        catalog.Register("CPYF", _ => { invoked = true; return CommandResult.Ok(); });
        var result = catalog.Execute(line);
        Assert.False(invoked);
        Assert.StartsWith("IPC0003", result.Message);
    }

    [Fact]
    public void Direct_calls_use_the_same_validation_as_text()
    {
        var catalog = new CommandCatalog(builtinContracts: true);
        catalog.Register("DSPJOB", _ => throw new InvalidOperationException("Must not execute"));
        Assert.StartsWith("IPC0003", catalog.Execute(CommandParser.Parse("DSPJOB JOB(OTHER)")).Message);
    }

    [Fact]
    public void Legacy_aliases_dispatch_to_canonical_handler_case_insensitively()
    {
        var catalog = new CommandCatalog(builtinContracts: true);
        catalog.Register("RMVM", c => CommandResult.Ok(c.GetOption("MBR")));
        Assert.Equal("TEST", catalog.Execute("rmvfm FILE(QGPL/SRC) MBR(TEST)").Message);
        Assert.Equal("TEST", catalog.Execute("RMVM FILE(QGPL/SRC) MBR(TEST)").Message);
    }

    [Fact]
    public void Literals_preserve_parentheses_spaces_and_escaped_apostrophes()
    {
        var call = CommandParser.Parse("CALL PGM(P) PARM('a)b(' 'it''s fine' '')");
        Assert.Equal(new[] { "a)b(", "it's fine", "" }, call.Split("PARM").Select(CommandParser.Unquote));
    }

    [Fact]
    public void Nested_commands_and_adjacent_keyword_groups_remain_intact()
    {
        var call = CommandParser.Parse("IF COND(&X *EQ 1) THEN(SNDMSG MSG('text)'))");
        var nested = CommandParser.Parse(call.GetOption("THEN")!);
        Assert.Equal("text)", CommandParser.Unquote(nested.GetOption("MSG")));
    }

    [Theory]
    [InlineData("GO MENU(MAIN))")]
    [InlineData("GO MENU(('unfinished))")]
    [InlineData("GO MENU(MAIN")]
    public void Malformed_nesting_is_rejected(string line) =>
        Assert.Throws<ClParseException>(() => CommandParser.Parse(line));
}
