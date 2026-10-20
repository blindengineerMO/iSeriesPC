using Ipc.Rpg.Runtime;
using Ipc.Rpg.Parsing;
using Ipc.Session;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData("P_")]
    [InlineData("p_")]
    [InlineData("'P_'")]
    public void Saved_disk_programs_use_name_or_literal_prefixes_after_source_removal(string prefix)
    {
        Compile("**free\ndcl-f DATA keyed prefix(" + prefix + ");\nchain 20 DATA;\nsndpgmmsg %char(P_ID)+':'+%trim(P_TEXT);\nseton LR;");
        _files.DeleteFile("QGPL","SOURCE"); using var session = Session(); Ok(session.Execute("CALL QGPL/APP"),"101:first");
    }
    [Theory]
    [InlineData("'p_'")]
    [InlineData("'Mixed_'")]
    [InlineData("'P-'")]
    public void Invalid_file_prefix_literals_report_the_declaration_location(string prefix)
    {
        var error = Assert.Throws<RpgCompileException>(() => Parse("**free\ndcl-f DATA prefix(" + prefix + ");\nreturn;"));
        Assert.Contains("PREFIX",error.Message); Assert.Contains("2",error.Message);
    }
}

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData("DSP_")]
    [InlineData("'DSP_'")]
    public void Saved_workstations_accept_file_prefix_literals_and_preserve_runtime_field_mapping(string prefix)
    {
        Display(Screen); Compile(File("prefix(" + prefix + ")") + "eval DSP_NAME='PREFIX';\nexfmt ENTRY;\nsndpgmmsg %trim(DSP_NAME);\nseton LR;");
        _sources.DeleteFile("QGPL","SOURCE");
        var terminal = new Terminal(display => display.Session.Handle(new(AidKey.Enter)));
        using var session = new ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR"),_timeout.Token,terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError,result.Message); Assert.Contains("PREFIX",result.Message);
    }
}
