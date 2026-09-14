namespace Ipc.Core.Objects;

public static class ObjectType
{
    public const string Library = "*LIB";
    public const string Program = "*PGM";
    public const string Module = "*MODULE";
    public const string ServiceProgram = "*SRVPGM";
    public const string Command = "*CMD";
    public const string File = "*FILE";
    public const string Menu = "*MENU";
    public const string MessageQueue = "*MSGQ";
    public const string DataQueue = "*DTAQ";
    public const string DataArea = "*DTAARA";
    public const string UserSpace = "*USRSPC";
    public const string OutputQueue = "*OUTQ";
    public const string JobDescription = "*JOBD";
    public const string JobQueue = "*JOBQ";
    public const string SubsystemDescription = "*SBSD";
    public const string Class = "*CLS";
    public const string UserProfile = "*USRPRF";
    public const string AuthorizationList = "*AUTL";
    public const string Journal = "*JRN";
    public const string JournalReceiver = "*JRNRCV";
    public const string DeviceDescription = "*DEVD";
    public const string ControllerDescription = "*CTLD";
    public const string LineDescription = "*LIND";
    public const string NetworkServer = "*NETSVR";
    public const string BindingDirectory = "*BNDDIR";
    public const string SourcePhysicalFile = "*SRCPF";
    public const string Symbol = "*SYM";
    public const string ServiceGroup = "*SFGMGT";
    public const string ProductionFile = "*PRTF";
    public const string DisplayFile = "*DSPF";
    public const string Query = "*QRYDFN";
    public const string QueryManagementQuery = "*QMQRY";
    public const string Documentation = "*DOC";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Library, Program, Module, ServiceProgram, Command, File, Menu, MessageQueue,
            DataQueue, DataArea, UserSpace, OutputQueue, JobDescription, JobQueue,
            SubsystemDescription, Class, UserProfile, AuthorizationList, Journal,
            JournalReceiver, DeviceDescription, ControllerDescription, LineDescription,
            NetworkServer, BindingDirectory, SourcePhysicalFile, Symbol, ServiceGroup,
            ProductionFile, DisplayFile, Query, QueryManagementQuery, Documentation,
        };

    public static bool IsKnown(string value) => All.Contains(value);

    public static string[] FormatsFor(string type) => type switch
    {
        File => new[] { "*DATA", "*SRC", "*DSPF", "*PRTF", "*LF", "*PF" },
        _ => new[] { "*" },
    };
}