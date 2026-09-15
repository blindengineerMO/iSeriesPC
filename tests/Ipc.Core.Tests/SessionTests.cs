using Ipc.Console.Session;
using Ipc.Services;
using Ipc.Terminal;
using Xunit;

namespace Ipc.Core.Tests.Session;

public class SessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IpcSystem _system;

    public SessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ipcsys-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _system = IpcSystem.Create(_tempDir, "test.db");
        _system.Start();
    }

    public void Dispose()
    {
        _system.Dispose();
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
        }
    }

    private static SignOnController SignOn(IpcSystem system)
    {
        var controller = new SignOnController(system);
        return controller;
    }

    [Fact]
    public void Sign_on_screen_renders_panel()
    {
        var controller = SignOn(_system);
        var panel = controller.Buffer.RowText(1);
        Assert.Contains("iSeriesPC", panel);
        var text = string.Concat(controller.Buffer.RowText(4), controller.Buffer.RowText(5));
        Assert.Contains("User", text);
        Assert.Contains("Password", text);
        Assert.Equal(SignOnState.SignOn, controller.State);
    }

    [Fact]
    public void Typed_characters_appear_in_fields()
    {
        var controller = SignOn(_system);
        var parser = new TerminalParser();

        foreach (var ch in "QSECOFR")
        {
            foreach (var key in parser.Feed(ch))
            {
                controller.Handle(key);
            }
        }

        Assert.Equal("QSECOFR", controller.SignOn.Form.ReadValue(0));
        Assert.Equal('Q', controller.Buffer[4, 29].Value);
    }

    [Fact]
    public void Bad_password_fails_sign_on()
    {
        var controller = SignOn(_system);
        FeedSignOn(controller, "QSECOFR", "WRONGPASS");

        Assert.Equal(SignOnState.Failed, controller.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F6_password_change_requires_current_credentials_before_creating_a_job(bool correct)
    {
        var controller = SignOn(_system);
        controller.SignOn.Form.WriteValue(0, "QSECOFR");
        controller.Handle(new KeyPress(AidKey.Pf6));
        Assert.Equal(SignOnState.ChangePassword, controller.State);
        var current = File.ReadAllText(Path.Combine(_tempDir, "test.db.initial-password")).Trim();
        FeedChangePassword(controller, correct ? current : "Wrong1", "Replacement1");
        Assert.Equal(correct ? SignOnState.SignedIn : SignOnState.ChangePassword, controller.State);
        Assert.Equal(correct ? 1 : 0, _system.Jobs.List().Count);
    }

    [Fact]
    public void Default_password_forces_password_change()
    {
        var controller = SignOn(_system);
        FeedSignOn(controller, "QSECOFR", File.ReadAllText(_tempDir + "/test.db.initial-password").Trim());

        Assert.Equal(SignOnState.ChangePassword, controller.State);
        var heading = controller.Buffer.RowText(3);
        Assert.Contains("Change", heading);
    }

    [Fact]
    public void Change_password_completes_sign_on()
    {
        var controller = SignOn(_system);
        FeedSignOn(controller, "QSECOFR", File.ReadAllText(_tempDir + "/test.db.initial-password").Trim());
        Assert.Equal(SignOnState.ChangePassword, controller.State);

        FeedChangePassword(controller, File.ReadAllText(_tempDir + "/test.db.initial-password").Trim(), "NEWPASS1");

        Assert.Equal(SignOnState.SignedIn, controller.State);

        var result = _system.Security.Authenticate("QSECOFR", "NEWPASS1");
        Assert.True(result.Success);
        Assert.False(result.MustChangePassword);
    }

    [Fact]
    public void Mismatched_new_passwords_are_rejected()
    {
        var controller = SignOn(_system);
        FeedSignOn(controller, "QSECOFR", File.ReadAllText(_tempDir + "/test.db.initial-password").Trim());

        FeedChangePassword(controller, File.ReadAllText(_tempDir + "/test.db.initial-password").Trim(), "NEWPASS1", verify: "OTHERPASS");

        Assert.Equal(SignOnState.ChangePassword, controller.State);
    }

    [Fact]
    public void Profile_disables_after_maximum_attempts()
    {
        for (var i = 0; i < 3; i++)
        {
            var controller = SignOn(_system);
            FeedSignOn(controller, "QSECOFR", "WRONGPASS");
            Assert.Equal(SignOnState.Failed, controller.State);
        }

        var result = _system.Security.Authenticate("QSECOFR", File.ReadAllText(_tempDir + "/test.db.initial-password").Trim());
        Assert.False(result.Success);
    }

    private static void FeedSignOn(SignOnController controller, string user, string password)
    {
        var parser = new TerminalParser();
        foreach (var ch in user)
        {
            foreach (var key in parser.Feed(ch))
            {
                controller.Handle(key);
            }
        }

        foreach (var key in parser.Feed('\t'))
        {
            controller.Handle(key);
        }

        foreach (var ch in password)
        {
            foreach (var key in parser.Feed(ch))
            {
                controller.Handle(key);
            }
        }

        foreach (var key in parser.Feed('\r'))
        {
            controller.Handle(key);
        }
    }

    private static void FeedChangePassword(SignOnController controller, string current, string next, string? verify = null)
    {
        var parser = new TerminalParser();
        FeedInto(parser, controller, current);
        foreach (var key in parser.Feed('\t'))
        {
            controller.Handle(key);
        }

        FeedInto(parser, controller, next);
        foreach (var key in parser.Feed('\t'))
        {
            controller.Handle(key);
        }

        FeedInto(parser, controller, verify ?? next);
        foreach (var key in parser.Feed('\r'))
        {
            controller.Handle(key);
        }
    }

    private static void FeedInto(TerminalParser parser, SignOnController controller, string text)
    {
        foreach (var ch in text)
        {
            foreach (var key in parser.Feed(ch))
            {
                controller.Handle(key);
            }
        }
    }
}

public class TerminalParserTests
{
    [Fact]
    public void Assemblies_multifragment_escape_sequences()
    {
        var parser = new TerminalParser();
        var keys = parser.Feed('\u001b');
        Assert.Empty(keys);

        keys = parser.Feed('O');
        Assert.Empty(keys);

        keys = parser.Feed('P');
        Assert.Single(keys);
        Assert.Equal(AidKey.Pf1, keys[0].Aid);
    }

    [Fact]
    public void Plain_chars_emit_immediately()
    {
        var parser = new TerminalParser();
        var keys = parser.Feed('A');
        Assert.Single(keys);
        Assert.Equal('A', keys[0].Character);
    }

    [Fact]
    public void Arrow_key_and_enter_produce_edits()
    {
        var parser = new TerminalParser();
        foreach (var ch in "\u001b[C")
        {
            parser.Feed(ch);
        }

        var keys = parser.Feed('\r');
        Assert.Single(keys);
        Assert.Equal(AidKey.Enter, keys[0].Aid);
    }
}
