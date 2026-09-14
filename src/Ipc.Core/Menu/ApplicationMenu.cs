namespace Ipc.Core.Menu;

public enum MenuOptionKind
{
    SubMenu,
    Command,
    Exit,
    SignOff,
    Prompt,
}

public sealed class MenuOption
{
    public required string Number { get; init; }

    public required string Text { get; init; }

    public required string Target { get; init; }

    public MenuOptionKind Kind { get; init; } = MenuOptionKind.SubMenu;
}

public sealed class ApplicationMenu
{
    public required string Name { get; init; }

    public string? Library { get; init; } = "QSYS";

    public string Title { get; init; } = string.Empty;

    public IReadOnlyList<MenuOption> Options { get; init; } = Array.Empty<MenuOption>();

    public MenuOption? Find(string number) =>
        Options.FirstOrDefault(o => string.Equals(o.Number, number, StringComparison.Ordinal));

    public override string ToString() => $"{Library}/{Name}";
}

public static class MenuNames
{
    public const string Main = "MAIN";
    public const string Major = "MAJOR";
    public const string SignOn = "QDSIGNON";
}

public static class SystemMenus
{
    public static ApplicationMenu Main() => new()
    {
        Name = MenuNames.Main,
        Library = "QSYS",
        Title = "M A I N",
        Options = new[]
        {
            new MenuOption { Number = "1", Text = "User tasks", Target = "USRTASK" },
            new MenuOption { Number = "2", Text = "Office tasks", Target = "OFFICE" },
            new MenuOption { Number = "3", Text = "General system tasks", Target = "GSYSTASK" },
            new MenuOption { Number = "4", Text = "Files, libraries, and folders", Target = "FILE" },
            new MenuOption { Number = "5", Text = "Programming", Target = "PROGDEV" },
            new MenuOption { Number = "6", Text = "Communications/Client Access", Target = "COMM" },
            new MenuOption { Number = "7", Text = "Define or change the system", Target = "CFG" },
            new MenuOption { Number = "8", Text = "Problem handling", Target = "PROBTASK" },
            new MenuOption { Number = "9", Text = "Display a menu", Target = "DSPMENU", Kind = MenuOptionKind.Prompt },
            new MenuOption { Number = "10", Text = "Messages", Target = "MSG", Kind = MenuOptionKind.Command },
            new MenuOption { Number = "11", Text = "Printer output", Target = "PRT" },
            new MenuOption { Number = "12", Text = "Problem handling", Target = "PROBTASK" },
            new MenuOption { Number = "90", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
        },
    };

    public static ApplicationMenu Major() => new()
    {
        Name = MenuNames.Major,
        Library = "QSYS",
        Title = "MAJOR",
        Options = new[]
        {
            new MenuOption { Number = "1", Text = "User tasks", Target = "USRTASK" },
            new MenuOption { Number = "2", Text = "Office tasks", Target = "OFFICE" },
            new MenuOption { Number = "3", Text = "General system tasks", Target = "GSYSTASK" },
            new MenuOption { Number = "4", Text = "Files, libraries, and folders", Target = "FILE" },
            new MenuOption { Number = "5", Text = "Programming", Target = "PROGDEV" },
            new MenuOption { Number = "6", Text = "Communications/Client Access", Target = "COMM" },
            new MenuOption { Number = "7", Text = "Define or change the system", Target = "CFG" },
            new MenuOption { Number = "8", Text = "Problem handling", Target = "PROBTASK" },
            new MenuOption { Number = "9", Text = "Display a menu", Target = "DSPMENU", Kind = MenuOptionKind.Prompt },
            new MenuOption { Number = "10", Text = "Messages", Target = "MSG", Kind = MenuOptionKind.Command },
            new MenuOption { Number = "11", Text = "Printer output", Target = "PRT" },
            new MenuOption { Number = "90", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff },
        },
    };
}