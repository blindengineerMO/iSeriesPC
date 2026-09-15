using Ipc.Console.Session;
using Ipc.Core.Menu;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Terminal;
using Xunit;

namespace Ipc.Core.Tests.Menu;

public class MenuSystemTests : IDisposable
{
    private readonly string _tempDir;
    private readonly IpcSystem _system;

    public MenuSystemTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ipcsys-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _system = IpcSystem.Create(_tempDir, "test.db");
        _system.Start();
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
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

    [Fact]
    public void Start_seeds_system_menus_as_objects()
    {
        Assert.NotNull(_system.Menus.TryGet(MenuNames.Main));
        Assert.NotNull(_system.Menus.TryGet(MenuNames.Major));

        var main = _system.Menus.Get(MenuNames.Main);
        Assert.NotEmpty(main.Options);

        var descriptor = _system.Objects.Get("QSYS", MenuNames.Main, "*MENU");
        Assert.NotNull(descriptor);
        Assert.Equal("*SBSMENU", descriptor.Attribute);
    }

    [Fact]
    public void Resolves_menu_by_name_preferring_QSYs()
    {
        var shadow = new ApplicationMenu
        {
            Name = MenuNames.Main,
            Library = "USR",
            Title = "SHADOW",
            Options = Array.Empty<MenuOption>(),
        };
        _system.Menus.Register(shadow, owner: "QSECOFR");

        Assert.Equal("QSYS", _system.Menus.Get(MenuNames.Main).Library);
        Assert.Equal("USR", _system.Menus.Get(MenuNames.Main, "USR").Library);
    }

    [Fact]
    public void Controller_renders_menu_title_and_options()
    {
        var profile = QsecOfr();
        var controller = new MenuController(_system, profile, "MAIN");

        Assert.Contains("AS/400 Main Menu", controller.Buffer.RowText(1));
        var body = string.Concat(Enumerable.Range(3, 18).Select(r => controller.Buffer.RowText(r)));
        Assert.Contains("Select one of the following:", body);
        Assert.Contains("1. User tasks", body);
        Assert.Contains("90. Sign off", body);
        Assert.Equal("QSYS/MAIN", controller.CurrentMenu);
    }

    [Fact]
    public void Navigates_into_custom_submenu_and_back()
    {
        RegisterMenuChain(out _);

        var controller = new MenuController(_system, QsecOfr(), "MYMENU");
        FeedText(controller, "1\r");

        Assert.Equal("MYLIB/SUB", controller.CurrentMenu);
        var body = string.Concat(Enumerable.Range(3, 18).Select(r => controller.Buffer.RowText(r)));
        Assert.Contains("Sub options", body);

        FeedKey(controller, KeyCodes.FunctionKeys[2]);
        Assert.Equal("MYLIB/MYMENU", controller.CurrentMenu);
    }

    [Fact]
    public void Invalid_selection_shows_error_and_stays()
    {
        RegisterMenuChain(out _);

        var controller = new MenuController(_system, QsecOfr(), "MYMENU");
        FeedText(controller, "7\r");

        Assert.Equal("MYLIB/MYMENU", controller.CurrentMenu);
        Assert.Contains("not valid", controller.Buffer.RowText(24));
    }

    [Fact]
    public void Sign_off_option_ends_session()
    {
        RegisterMenuChain(out _);

        var controller = new MenuController(_system, QsecOfr(), "MYMENU");
        var evt = FeedText(controller, "2\r");

        Assert.True(evt.Last!.EndSession);
        Assert.Equal("Sign off.", evt.Last.Message);
    }

    [Fact]
    public void F3_on_top_level_menu_signs_off()
    {
        RegisterMenuChain(out _);

        var controller = new MenuController(_system, QsecOfr(), "MYMENU");
        var evt = FeedKey(controller, KeyCodes.FunctionKeys[2]);

        Assert.True(evt.Last!.EndSession);
    }

    [Fact]
    public void Display_a_menu_option_prompts_for_target()
    {
        var controller = new MenuController(_system, QsecOfr(), "MAIN");
        var evt = FeedText(controller, "9\r");
        Assert.Contains("Prompt command - GO", controller.Buffer.RowText(1));

        evt = FeedText(controller, "MAJOR\r");
        Assert.False(evt.Last!.EndSession);
        Assert.Equal("QSYS/MAJOR", controller.CurrentMenu);
    }

    [Fact]
    public void Missing_prompt_target_reports_error()
    {
        var controller = new MenuController(_system, QsecOfr(), "MAIN");
        FeedText(controller, "9\r");

        var evt = FeedText(controller, "NOPE\r");
        Assert.False(evt.Last!.EndSession);
        Assert.Contains("not found", controller.Buffer.RowText(24));
        Assert.Equal("QSYS/MAIN", controller.CurrentMenu);
    }

    [Fact]
    public void Qualified_target_navigates_across_libraries()
    {
        RegisterMenuChain(out _);

        _system.Libraries.CreateLibrary("OTHER");
        _system.Menus.Register(new ApplicationMenu
        {
            Name = "ELSE",
            Library = "OTHER",
            Title = "ELSE MENU",
            Options = new[]
            {
                new MenuOption { Number = "2", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
            },
        }, owner: "QSECOFR");

        _system.Menus.Register(new ApplicationMenu
        {
            Name = "BRIDGE",
            Library = "MYLIB",
            Title = "BRIDGE MENU",
            Options = new[]
            {
                new MenuOption { Number = "1", Text = "Elsewhere", Target = "OTHER/ELSE" },
                new MenuOption { Number = "2", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
            },
        }, owner: "QSECOFR");

        Assert.Equal("OTHER", _system.Menus.Go("OTHER/ELSE").Library);

        var controller = new MenuController(_system, QsecOfr(), "BRIDGE");
        FeedText(controller, "1\r");
        Assert.Equal("OTHER/ELSE", controller.CurrentMenu);
    }

    private UserProfile QsecOfr() =>
        _system.Security.Profiles.Get(ProfileNames.QSecOficer);

    private void RegisterMenuChain(out string mainName)
    {
        mainName = "MYMENU";
        _system.Libraries.CreateLibrary("MYLIB");
        var profile = QsecOfr();
        profile.InitialCurrentLibrary = "MYLIB";
        _system.Security.Profiles.Update(profile);
        _system.Menus.Register(new ApplicationMenu
        {
            Name = "SUB",
            Library = "MYLIB",
            Title = "SUB MENU",
            Options = new[]
            {
                new MenuOption { Number = "1", Text = "Sub options", Target = "NOTHING" },
                new MenuOption { Number = "2", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
            },
        }, owner: "QSECOFR");

        _system.Menus.Register(new ApplicationMenu
        {
            Name = mainName,
            Library = "MYLIB",
            Title = "MY MENU",
            Options = new[]
            {
                new MenuOption { Number = "1", Text = "Down a level", Target = "SUB" },
                new MenuOption { Number = "2", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
            },
        }, owner: "QSECOFR");
    }

    private static (SessionEvent? Last, List<SessionEvent> All) FeedText(ISessionController controller, string text)
    {
        var parser = new TerminalParser();
        var all = new List<SessionEvent>();
        SessionEvent? last = null;
        foreach (var ch in text)
        {
            foreach (var key in parser.Feed(ch))
            {
                last = controller.Handle(key);
                all.Add(last);
            }
        }

        return (last, all);
    }

    private static (SessionEvent? Last, List<SessionEvent> All) FeedKey(ISessionController controller, string sequence)
    {
        var parser = new TerminalParser();
        var all = new List<SessionEvent>();
        SessionEvent? last = null;
        foreach (var ch in sequence)
        {
            foreach (var key in parser.Feed(ch))
            {
                last = controller.Handle(key);
                all.Add(last);
            }
        }

        return (last, all);
    }
}