using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Dsp;
using Ipc.Services;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed class CommandPromptTests
{
    private static void Type(Func<KeyPress, CommandPromptResponse> handle, string value)
    { var parser = new TerminalParser(); foreach (var c in value) foreach (var key in parser.Feed(c)) handle(key); }
    [Fact]
    public void Prompt_quotes_text_preserves_nested_values_and_rejects_parameter_injection()
    {
        var metadata = new CommandMetadata("EXAMPLE", new[] { new CommandParameter("CMD", "Command"), new CommandParameter("TEXT", "Description", Literal: true) });
        var prompt = new CommandPromptSession(metadata, CommandParser.Parse("EXAMPLE CMD(CALL PGM(QGPL/A)) TEXT('Owner''s text')"));
        Assert.Equal("EXAMPLE CMD(CALL PGM(QGPL/A)) TEXT('Owner''s text')", prompt.Build());
        Type(prompt.Handle, "\u001b[H\u000bDSPJOB) TEXT(INJECTED");
        Assert.Null(prompt.Handle(new(AidKey.Enter)).Command); Assert.Contains("parenthes", prompt.Buffer.RowText(24), StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void Paged_parameters_keep_values_hide_secrets_and_do_not_truncate_long_initial_values()
    {
        var parameters = Enumerable.Range(1, 14).Select(i => new CommandParameter("P" + i, "Parameter " + i, Secret: i == 14)).ToArray();
        var prompt = new CommandPromptSession(new("EXAMPLE", parameters), CommandParser.Parse("EXAMPLE P1(" + new string('X', 100) + ") P14(secret-value)"));
        Assert.Contains(new string('X', 100), prompt.Build());
        prompt.Handle(new(AidKey.RollUp)); Assert.DoesNotContain("secret-value", new AnsiRenderer().Render(prompt.Buffer));
        prompt.Error("Rejected secret-value"); Assert.Contains("[redacted]", prompt.Buffer.RowText(24));
        Type(prompt.Handle, "FIRST"); prompt.Handle(new(AidKey.RollDown)); Assert.Contains("P13(FIRST)", prompt.Build());
        Assert.Throws<ArgumentException>(() => new CommandPromptSession(new("EXAMPLE", parameters), CommandParser.Parse("EXAMPLE P1(" + new string('x', 1025) + ")")));
    }
    [Fact]
    public void Menu_F4_cancels_restores_help_and_runs_the_shared_command_with_authority_checks()
    {
        using var system = IpcSystem.Create(":memory:"); system.Start(); system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "AdminPassword22");
        using var menu = new MenuController(system, system.Security.Profiles.Get("QSECOFR"));
        void Type(string value) { var parser = new TerminalParser(); foreach (var c in value) foreach (var key in parser.Feed(c)) menu.Handle(key); }
        Type("CRTLIB LIB(F4LIB)"); var before = menu.Buffer.Clone();
        menu.Handle(new(AidKey.Pf4)); Assert.Contains("Prompt command - CRTLIB", menu.Buffer.RowText(1));
        menu.Handle(new(AidKey.Pf1)); Assert.Contains("CRTLIB help", menu.Buffer.RowText(1)); menu.Handle(new(AidKey.Pf3));
        Assert.Contains("Prompt command - CRTLIB", menu.Buffer.RowText(1)); menu.Handle(new(AidKey.Pf12));
        Assert.Equal(before.RowText(23), menu.Buffer.RowText(23)); Assert.Equal(before.Cursor, menu.Buffer.Cursor);
        menu.Handle(new(AidKey.Pf4)); menu.Handle(new(AidKey.Enter)); Assert.True(system.Objects.Exists("QSYS", "F4LIB", ObjectType.Library));
        Assert.DoesNotContain("Prompt command", menu.Buffer.RowText(1));
        using var user = new MenuController(system, system.Security.Profiles.Get("QUSER"));
        foreach (var c in "CRTLIB LIB(DENIED)") user.Handle(new(AidKey.None, Character: c)); user.Handle(new(AidKey.Pf4)); user.Handle(new(AidKey.Enter));
        Assert.Contains("Prompt command", user.Buffer.RowText(1)); Assert.Contains("CPF9802", user.Buffer.RowText(24)); Assert.False(system.Objects.Exists("QSYS", "DENIED", ObjectType.Library));
    }
}
