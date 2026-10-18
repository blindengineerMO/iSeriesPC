using System.Text.Json.Nodes;
using Ipc.Core.Objects;
using Ipc.Dsp;
using Ipc.Rpg.Model;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDisplayFileTests
{
    private static string IndicatorScreen => Dds(keywords: "INDARA CA03(03) CF06(06)") + Dds("ENTRY", record: true) +
        Dds(row: 2, column: 2, keywords: "'LOCAL INDICATOR VISIBLE'", indicators: " 21") + Dds("NAME", 10, row: 4, column: 2, usage: 'B');
    private const string Keys = "dcl-ds KEYS qualified;\nF3 ind pos(3);\nF6 ind pos(6);\nSHOW ind pos(21);\nend-ds;\n";

    [Theory]
    [InlineData(37, true)]
    [InlineData(37, false)]
    [InlineData(1208, true)]
    [InlineData(1208, false)]
    public void INDDS_real_CA_CF_exchange_uses_local_conditioning_and_response_bytes(int ccsid, bool attention)
    {
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        Display(IndicatorScreen); Compile(File("indds(KEYS)") + Keys +
            "eval KEYS.SHOW=*on;\neval NAME='ORIGINAL';\nexfmt ENTRY;\n" +
            "if KEYS.SHOW and not *in21 and not *in03 and not *in06;\n" +
            "if KEYS.F3 and NAME='ORIGINAL';\nsndpgmmsg 'INDDS CA';\nendif;\n" +
            "if KEYS.F6 and NAME='XORIGINAL';\nsndpgmmsg 'INDDS CF';\nendif;\nendif;\nseton LR;");
        var terminal = new Terminal(display =>
        {
            Assert.Contains("LOCAL INDICATOR VISIBLE", display.Session.Buffer.RowText(2));
            display.Session.Handle(new(AidKey.None, Character: 'X'));
            return display.Session.Handle(new(attention ? AidKey.Pf3 : AidKey.Pf6));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, terminal);
        Assert.Equal(ccsid, session.Job.Ccsid);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains(attention ? "INDDS CA" : "INDDS CF", result.Message);
    }
    [Fact]
    public void INDDS_without_DDS_INDARA_is_rejected_before_creating_a_program()
    {
        Display(Screen); var result = Create(File("indds(KEYS)") + Keys + "return;");
        Assert.True(result.IsError); Assert.Contains("INDARA", result.Message); Assert.False(_system.Objects.Exists("QGPL", "APP", ObjectType.Program));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Version_two_bindings_detect_INDARA_changes_even_without_an_explicit_INDDS(bool local)
    {
        Display(IndicatorScreen); Compile(File(local ? "indds(KEYS)" : "") + Keys + "exfmt ENTRY;\nseton LR;");
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        Assert.Equal(2, JsonNode.Parse(descriptor.ExtendedAttributes![RpgWorkstationBindings.AttributeName])!["version"]!.GetValue<int>());
        new DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", IndicatorScreen.Replace("INDARA ", ""), replace: true);
        var calls = 0; var terminal = new Terminal(display => { calls++; return display.Session.Handle(new(AidKey.Enter)); });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.True(result.IsError); Assert.Contains("recompile", result.Message); Assert.Equal(0, calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Legacy_version_one_bindings_still_run_against_live_display_files(bool indicatorArea)
    {
        Display(indicatorArea ? IndicatorScreen : Screen); Compile(File() + "exfmt ENTRY;\nsndpgmmsg 'LEGACY DISPLAY';\nseton LR;");
        var descriptor = _system.Objects.GetRequired("QGPL", "APP", ObjectType.Program);
        var original = RpgWorkstationBindings.Restore(descriptor.Source!, descriptor.ExtendedAttributes![RpgWorkstationBindings.AttributeName]);
        var node = JsonNode.Parse(descriptor.ExtendedAttributes![RpgWorkstationBindings.AttributeName])!; node["version"] = 1;
        var layout = node["files"]![0]!["layout"]!; layout.AsObject().Remove("indicatorArea");
        layout["signature"] = RpgWorkstationLayout.SignatureFor(original.Files.Single().Layout.Records);
        descriptor.ExtendedAttributes = new Dictionary<string, string>(descriptor.ExtendedAttributes!) { [RpgWorkstationBindings.AttributeName] = node.ToJsonString() };
        _system.Objects.Update(descriptor);
        var terminal = new Terminal(display => display.Session.Handle(new(AidKey.Enter)));
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR"), _timeout.Token, terminal);
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("LEGACY DISPLAY", result.Message);
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Saved_INDDS_and_retained_calls_preserve_conditioning_until_LR_reclaims_the_bank(int ccsid)
    {
        var profile = _system.Security.Profiles.Get("QSECOFR"); profile.Ccsid = ccsid; _system.Security.Profiles.Update(profile);
        Display(IndicatorScreen); Compile(File("indds(KEYS)") + Keys + "dcl-s COUNT int(10) inz(0);\neval COUNT=COUNT+1;\n" +
            "if COUNT=1 and KEYS.SHOW;\nsndpgmmsg 'BAD REINITIALIZATION';\nseton LR;\nreturn;\nendif;\neval KEYS.SHOW=*on;\nexfmt ENTRY;\n" +
            "sndpgmmsg %char(COUNT)+':'+%char(KEYS.SHOW)+':'+%char(KEYS.F6)+':'+%char(*in06);\nif COUNT=2;\nseton LR;\nendif;\nreturn;");
        _sources.RemoveMember("QGPL", "SOURCE", "APP");
        var calls = 0; var terminal = new Terminal(display =>
        {
            Assert.Contains("LOCAL INDICATOR VISIBLE", display.Session.Buffer.RowText(2));
            return display.Session.Handle(new(++calls == 1 ? AidKey.Pf6 : AidKey.Enter));
        });
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, terminal);
        Assert.Equal(ccsid, session.Job.Ccsid);
        var first = session.Execute("CALL QGPL/APP"); Assert.False(first.IsError, first.Message); Assert.Contains("1:1:1:0", first.Message);
        var second = session.Execute("CALL QGPL/APP"); Assert.False(second.IsError, second.Message); Assert.Contains("2:1:0:0", second.Message);
        var fresh = session.Execute("CALL QGPL/APP"); Assert.False(fresh.IsError, fresh.Message); Assert.Contains("1:1:0:0", fresh.Message); Assert.Equal(3, calls);
    }
    [Fact]
    public void INDARA_cannot_be_conditioned_in_DDS()
    {
        var error = Assert.Throws<PanelCompileException>(() => new DisplayDdsCompiler().Compile("BAD", Dds(keywords: "INDARA", indicators: " 21") + Dds("ENTRY", record: true)));
        Assert.Contains("unconditional", error.Message);
    }
}
