namespace Ipc.Core.System;

public enum SystemValueType
{
    Character,
    Numeric,
    Date,
    Time,
    Timestamp,
}

public sealed class SystemValue
{
    public required string Name { get; init; }

    public required SystemValueType ValueType { get; init; }

    public required string Value { get; set; }

    public string? Description { get; init; }

    public override string ToString() => $"{Name}({Value})";
}

public static class SystemValueNames
{
    public const string SystemName = "QSYSNAME";
    public const string Ccsid = "QCCSID";
    public const string Date = "QDATE";
    public const string Time = "QTIME";
    public const string DateFormat = "QDATFMT";
    public const string DateSeparator = "QDATSEP";
    public const string TimeFormat = "QTIMEFMT";
    public const string TimeSeparator = "QTIMSEP";
    public const string CenturyValue = "QCENTURY";

    public const string SecurityLevel = "QSECURITY";
    public const string PasswordSystemLevel = "QPWDLVL";
    public const string PasswordMinimumLength = "QPWDMINLEN";
    public const string PasswordRequiredDigit = "QPWDRQDDGT";
    public const string PasswordRepeatedCharacters = "QPWDRQDRPT";
    public const string PasswordChangedDays = "QPWDCHGDAY";
    public const string PasswordExpirationInterval = "QPWDEXPITV";
    public const string MaximumSignOnAttempts = "QMAXSIGN";
    public const string SystemValueMaximumSignOnAttempts = "QMAXSGNACN";

    public const string InitialMenu = "QINLMNU";
    public const string InitialProgram = "QINLPGM";
    public const string InitialCurrentLibrary = "QINLCURLIB";
    public const string LibraryList = "QLIBL";
    public const string SystemLibraryList = "QSYSLIBL";
    public const string UserLibraryList = "QUSRLIBL";
    public const string SignOnDisplay = "QSIGNON";

    public const string CommandLine = "QCMDLIN";
    public const string MessageQueue = "QLOGMSGQ";
    public const string PrintText = "QPRTTXT";
    public const string OutputQueueForPrinter = "QOUTQ";
    public const string CurrentLibrary = "QCURLIB";

    public const string DayOfWeekRule = "QDOWRSTCFG";
    public const string SignOffDisplay = "QSIGNOFF";
    public const string KeyboardLed = "QKBDBUF";
    public const string ConsoleDevice = "QCONSOLE";
    public const string AllowAddSpecialAuthorities = "QALWUSRDMN";
    public const string AllowUserProfilesToChangePassword = "QALWCHGUSRPWD";
    public const string MaximumUnsignedValues = "QMAXSPOOL";
    public const string MaxNumberJobs = "QMAXJOB";
    public const string SubmitMaximumJobs = "QSBMJOBMSGQ";
    public const string JobLogOutput = "QJOBLOG";
}

public sealed class SystemValueRegistry
{
    private readonly Dictionary<string, SystemValue> _values;

    public SystemValueRegistry()
    {
        var now = DateTimeOffset.Now;
        _values = Defaults(now).ToDictionary(v => v.Name, StringComparer.Ordinal);
    }

    public static IReadOnlyList<SystemValue> Defaults(DateTimeOffset now) => new List<SystemValue>
    {
        V(SystemValueNames.SystemName, "...SYS", SystemValueType.Character, "System name"),
        V(SystemValueNames.Ccsid, "37", SystemValueType.Numeric, "Primary CCSID"),
        V(SystemValueNames.Date, now.ToString("yyyy-MM-dd"), SystemValueType.Date),
        V(SystemValueNames.Time, now.ToString("HH:mm:ss"), SystemValueType.Time),
        V(SystemValueNames.DateFormat, "*YMD", SystemValueType.Character, "Date format"),
        V(SystemValueNames.DateSeparator, "/", SystemValueType.Character, "Date separator"),
        V(SystemValueNames.TimeFormat, "*HMS", SystemValueType.Character, "Time format"),
        V(SystemValueNames.TimeSeparator, ":"),
        V(SystemValueNames.SecurityLevel, "40", SystemValueType.Numeric, "Security level"),
        V(SystemValueNames.PasswordSystemLevel, "*SYSVAL", SystemValueType.Character, "Password level follows QSECURITY"),
        V(SystemValueNames.PasswordMinimumLength, "6", SystemValueType.Numeric),
        V(SystemValueNames.PasswordRequiredDigit, "*NO", SystemValueType.Character, "Require digit in passwords"),
        V(SystemValueNames.PasswordRepeatedCharacters, "*NO", SystemValueType.Character, "Repeated characters not allowed"),
        V(SystemValueNames.PasswordExpirationInterval, "30", SystemValueType.Numeric, "Days between password changes"),
        V(SystemValueNames.MaximumSignOnAttempts, "3", SystemValueType.Numeric),
        V(SystemValueNames.InitialMenu, "*MAIN", SystemValueType.Character, "Initial menu"),
        V(SystemValueNames.InitialProgram, "*NONE"),
        V(SystemValueNames.InitialCurrentLibrary, "*NONE"),
        V(SystemValueNames.LibraryList, "*QSYSLIBL"),
        V(SystemValueNames.SystemLibraryList, "QSYS,QSYS2,QUSRSYS,QHLPSYS,QSPL"),
        V(SystemValueNames.UserLibraryList, ""),
        V(SystemValueNames.SignOnDisplay, "*SYSVAL"),
        V(SystemValueNames.CommandLine, "1", SystemValueType.Numeric),
        V(SystemValueNames.MessageQueue, "QSYSOPR"),
        V(SystemValueNames.PrintText, ""),
        V(SystemValueNames.OutputQueueForPrinter, "QGPL"),
        V(SystemValueNames.JobLogOutput, "*SECLVL"),
    };

    private static SystemValue V(
        string name, string value,
        SystemValueType type = SystemValueType.Character,
        string? description = null) =>
        new() { Name = name, Value = value, ValueType = type, Description = description };

    public SystemValue Get(string name)
    {
        if (!_values.TryGetValue(name, out var v))
        {
            throw new KeyNotFoundException($"System value '{name}' is not defined.");
        }

        return v;
    }

    public bool TryGet(string name, out SystemValue? value) =>
        _values.TryGetValue(name, out value);

    public void Set(string name, string value)
    {
        if (!_values.TryGetValue(name, out var v))
        {
            throw new KeyNotFoundException($"System value '{name}' is not defined.");
        }

        v.Value = value;
    }

    public IReadOnlyList<SystemValue> All => _values.Values.OrderBy(v => v.Name).ToList();
}