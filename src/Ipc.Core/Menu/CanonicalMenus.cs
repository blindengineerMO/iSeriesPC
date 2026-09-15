namespace Ipc.Core.Menu;

public static partial class SystemMenus
{
    private static MenuOption Sub(string number, string text, string menu) => new() { Number = number, Text = text, Target = menu, Kind = MenuOptionKind.SubMenu };
    private static MenuOption Run(string number, string text, string command, bool prompt = false) => new() { Number = number, Text = text, Target = command, Kind = prompt ? MenuOptionKind.Prompt : MenuOptionKind.Command };
    private static ApplicationMenu Menu(string name, string title, params MenuOption[] options) => new() { Name = name, Library = "QSYS", Title = title, Options = options };
    public static ApplicationMenu Main() => Menu("MAIN", "AS/400 Main Menu",
        Sub("1", "User tasks", "USRTASK"), Sub("2", "Office tasks", "OFFICE"), Sub("3", "General system tasks", "SYSTEM"),
        Sub("4", "Files, libraries, and folders", "DATA"), Sub("5", "Programming", "PROGRAM"), Sub("6", "Communications", "COMM"),
        Sub("7", "Define or change the system", "DEFINE"), Sub("8", "Problem handling", "PROBLEM"), Run("9", "Display a menu", "GO", true),
        Sub("10", "Information Assistant options", "INFOAST"), Sub("11", "Client Access/400 tasks", "CLIENT"),
        new MenuOption { Number = "90", Text = "Sign off", Target = "SIGNOFF", Kind = MenuOptionKind.SignOff });
    public static ApplicationMenu Major() => Menu("MAJOR", "Major Command Groups",
        Run("1", "Select Command by Name", "SLTCMD"), Sub("2", "Verb Commands", "VERB"), Sub("3", "Subject Commands", "SUBJECT"),
        Sub("4", "Object Management Commands", "CMDOBJMGT"), Sub("5", "File Commands", "CMDFILE"), Sub("6", "Save and Restore Commands", "CMDSAVRST"),
        Sub("7", "Work Management Commands", "CMDWRKMGT"), Sub("8", "Data Management Commands", "CMDDTAMGT"), Sub("9", "Security Commands", "CMDSEC"),
        Sub("10", "Print Commands", "CMDPRT"), Sub("11", "Spooling Commands", "CMDSPL"), Sub("12", "System Control Commands", "CMDSYSCTL"), Sub("13", "Program Commands", "CMDPGM"));
    public static IReadOnlyList<ApplicationMenu> All()
    {
        var menus = new List<ApplicationMenu> { Main(), Major(), SystemRequest(),
            Menu("USRTASK", "User Tasks", Run("1", "Display current job", "DSPJOB"), Run("2", "Work with submitted jobs", "WRKSBMJOB"), Run("3", "Select group job", "TFRGRPJOB GRPJOB(*SELECT)"), Sub("4", "Printer output", "PRT"), Sub("5", "Messages", "MSG")),
            Menu("OFFICE", "Office Tasks", Run("1", "Work with files", "WRKOBJ OBJ(*LIBL/*ALL) OBJTYPE(*FILE)"), Sub("2", "Messages", "MSG"), Sub("3", "Printer output", "PRT")),
            Menu("SYSTEM", "General System Tasks", Sub("1", "Jobs", "JOB"), Run("2", "System status", "WRKSYSSTS"), Sub("4", "Messages", "MSG"), Sub("5", "Files, libraries, and folders", "DATA"), Sub("9", "Communications", "COMM"), Sub("10", "Security", "CMDSEC")),
            Menu("DATA", "Files, Libraries, and Folders", Run("1", "Work with libraries", "WRKLIB"), Run("2", "Work with objects", "WRKOBJ"), Run("3", "Display file data", "DSPPFM", true), Run("4", "Create a library", "CRTLIB", true), Sub("5", "File commands", "CMDFILE")),
            Menu("PROGRAM", "Programming", Run("1", "Work with programs", "WRKOBJ OBJ(*LIBL/*ALL) OBJTYPE(*PGM)"), Run("2", "Design a screen", "STRSDA", true), Run("3", "Compile CL", "CRTCLPGM", true), Run("4", "Compile RPG", "CRTBNDRPG", true), Run("5", "Call a program", "CALL", true), Sub("6", "Command groups", "MAJOR")),
            Menu("COMM", "Communications", Run("1", "Communication jobs", "WRKACTJOB SBS(QSERVER)"), Run("2", "Certificate inventory", "WRKCERT"), Run("3", "Identity mappings", "DSPEIMMAP", true), Run("4", "Runtime status", "WRKSYSSTS")),
            Menu("DEFINE", "Define or Change the System", Run("1", "System values", "WRKSYSVAL"), Run("2", "User profiles", "WRKUSRPRF"), Run("3", "Subsystems", "WRKSBS"), Run("4", "Job descriptions", "WRKJOBD"), Run("5", "Classes", "WRKCLS"), Sub("6", "System work menus", "WRKSYS")),
            Menu("PROBLEM", "Problem Handling", Run("1", "Current job log", "DSPJOBLOG"), Run("2", "Active jobs", "WRKACTJOB"), Run("3", "Job details", "WRKJOB"), Run("4", "Runtime status", "WRKSYSSTS")),
            Menu("INFOAST", "Information Assistant Options", Run("1", "Select a command", "SLTCMD"), Run("2", "Work with menus", "WRKOBJ OBJ(*ALL/*ALL) OBJTYPE(*MENU)"), Sub("3", "Major command groups", "MAJOR")),
            Menu("CLIENT", "Client Access/400 Tasks", Run("1", "Communication jobs", "WRKACTJOB SBS(QSERVER)"), Run("2", "Client certificates", "WRKCERT"), Sub("3", "Files and data", "DATA")),
            Menu("JOB", "Jobs", Run("1", "Active jobs", "WRKACTJOB"), Run("2", "Current job", "WRKJOB"), Run("3", "Submitted jobs", "WRKSBMJOB"), Run("4", "Job queues", "WRKJOBQ"), Run("5", "Submit a job", "SBMJOB", true), Run("6", "Job descriptions", "WRKJOBD"), Run("7", "Classes", "WRKCLS")),
            Menu("MSG", "Messages", Run("1", "Message queue definitions", "WRKMSGQ"), Run("2", "Message file definitions", "WRKOBJ OBJ(*ALL/*ALL) OBJTYPE(*MSGF)"), Run("3", "Create a message file", "CRTMSGF", true), Run("4", "Add a message description", "ADDMSGD", true)),
            Menu("PRT", "Printer Output", Run("1", "Output queue definitions", "WRKOUTQ"), Run("2", "Current job output routing", "WRKJOB"), Run("3", "Change job output routing", "CHGJOB", true)),
            Menu("CMDOBJMGT", "Object Management Commands", Run("1", "Work with objects", "WRKOBJ"), Run("2", "Create library", "CRTLIB", true), Run("3", "Duplicate object", "CRTDUPOBJ", true), Run("4", "Rename object", "RNMOBJ", true), Run("5", "Move object", "MOVOBJ", true), Run("6", "Display object", "DSPOBJD", true)),
            Menu("CMDFILE", "File Commands", Run("1", "Create source file", "CRTSRCPF", true), Run("2", "Create physical file", "CRTPF", true), Run("3", "Copy file", "CPYF", true), Run("4", "Display file description", "DSPFD", true), Run("5", "Display field descriptions", "DSPFFD", true), Run("6", "Add file member", "ADDPFM", true)),
            Menu("CMDSAVRST", "Save and Restore Commands", Run("1", "Restore signed code package", "RSTSGNOBJ", true), Run("2", "Apply signed update", "APYSGNUPD", true), Run("3", "Select save commands", "SLTCMD CMD(SAV*)"), Run("4", "Select restore commands", "SLTCMD CMD(RST*)")),
            Menu("CMDWRKMGT", "Work Management Commands", Sub("1", "Jobs", "JOB"), Run("2", "Subsystems", "WRKSBS"), Run("3", "Create job queue", "CRTJOBQ", true), Run("4", "Create job description", "CRTJOBD", true), Run("5", "Create class", "CRTCLS", true)),
            Menu("CMDDTAMGT", "Data Management Commands", Run("1", "Display file data", "DSPPFM", true), Run("2", "Copy file", "CPYF", true), Run("3", "Display job data area", "DSPDTAARA", true), Run("4", "Change job data area", "CHGDTAARA", true), Run("5", "Display file overrides", "DSPOVR")),
            Menu("CMDSEC", "Security Commands", Run("1", "Work with user profiles", "WRKUSRPRF"), Run("2", "Create user profile", "CRTUSRPRF", true), Run("3", "Work with certificates", "WRKCERT"), Run("4", "Work with key ring", "WRKKEYRING"), Run("5", "Check signed object", "CHKOBJITG", true)),
            Menu("CMDPRT", "Print Commands", Sub("1", "Printer output", "PRT"), Run("2", "Select printer commands", "SLTCMD CMD(*PRT*)")),
            Menu("CMDSPL", "Spooling Commands", Run("1", "Output queue definitions", "WRKOUTQ"), Run("2", "Select spool commands", "SLTCMD CMD(*SPL*)")),
            Menu("CMDSYSCTL", "System Control Commands", Run("1", "System status", "WRKSYSSTS"), Run("2", "System values", "WRKSYSVAL"), Run("3", "Subsystems", "WRKSBS"), Run("4", "Display a menu", "GO", true)),
            Menu("CMDPGM", "Program Commands", Sub("1", "Programming", "PROGRAM"), Run("2", "Display program", "DSPPGM", true), Run("3", "Reclaim activation group", "RCLACTGRP", true)),
            Menu("WRKSYS", "Work with System", Sub("1", "System jobs", "WRKSYSJOB"), Sub("2", "System security", "WRKSYSSEC"), Sub("3", "System configuration", "WRKSYSCFG"), Run("4", "Runtime status", "WRKSYSSTS")),
            Menu("WRKSYSJOB", "System Jobs", Sub("1", "Jobs", "JOB"), Run("2", "Subsystems", "WRKSBS"), Run("3", "Job queues", "WRKJOBQ")),
            Menu("WRKSYSSEC", "System Security", Sub("1", "Security commands", "CMDSEC"), Run("2", "System values", "WRKSYSVAL")),
            Menu("WRKSYSCFG", "System Configuration", Run("1", "System values", "WRKSYSVAL"), Run("2", "Job descriptions", "WRKJOBD"), Run("3", "Classes", "WRKCLS")),
        };
        var verbs = new[] { "CRT", "DSP", "WRK", "CHG", "DLT", "ADD", "RMV", "STR", "END", "HLD", "RLS", "SAV", "RST", "TFR" };
        menus.Add(Menu("VERB", "Verb Commands", verbs.Select((verb, i) => Run((i + 1).ToString(), verb + " commands", "SLTCMD CMD(" + verb + "*)")).ToArray()));
        var subjects = new[] { "OBJ", "FILE", "PGM", "JOB", "USRPRF", "MENU", "MSG", "OUTQ", "SBS", "SYSVAL", "CERT" };
        menus.Add(Menu("SUBJECT", "Subject Commands", subjects.Select((subject, i) => Run((i + 1).ToString(), subject + " commands", "SLTCMD CMD(*" + subject + "*)")).ToArray()));
        foreach (var (alias, target) in new[] { ("GSYSTASK", "SYSTEM"), ("FILE", "DATA"), ("PROGDEV", "PROGRAM"), ("CFG", "DEFINE"), ("PROBTASK", "PROBLEM") })
        {
            var source = menus.Single(m => m.Name == target); menus.Add(Menu(alias, source.Title, source.Options.ToArray()));
        }
        return menus;
    }
    public static bool IsLegacyDefault(ApplicationMenu menu)
    {
        var old = menu.Name == "MAIN" ? LegacyMain() : menu.Name == "MAJOR" ? LegacyMajor() : null;
        return old is not null && menu.Library == old.Library && menu.Title == old.Title && menu.Options.Count == old.Options.Count &&
            menu.Options.Zip(old.Options).All(pair => pair.First.Number == pair.Second.Number && pair.First.Text == pair.Second.Text && pair.First.Target == pair.Second.Target && pair.First.Kind == pair.Second.Kind && pair.First.RequiredAuthority == pair.Second.RequiredAuthority);
    }
}
