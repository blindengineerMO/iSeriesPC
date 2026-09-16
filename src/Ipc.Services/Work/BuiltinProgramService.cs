using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

/// <summary>Versioned catalog identities for programs implemented by the shared runtime.</summary>
public static class BuiltinProgramService
{
    public const string Attribute = "IPCAPI";
    private const string QcmdexcSource = "{\"api\":\"QCMDEXC\",\"version\":1}";

    public static void SeedDefaults(SqliteConnectionFactory factory)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        var objects = new SqliteObjectStore(factory);
        // Existing objects, including signed or locally replaced programs, are never overwritten.
        if (objects.GetForAuthorization("QSYS", "QCMDEXC", ObjectType.Program) is null)
            objects.Create(new ObjectDescriptor { Key = new("QSYS", "QCMDEXC"), ObjectType = ObjectType.Program,
                Owner = "QSYS", Attribute = Attribute, Source = QcmdexcSource, Description = "Execute command API" });
    }

    public static void Validate(ObjectDescriptor descriptor)
    {
        if (descriptor.ObjectType != ObjectType.Program || descriptor.Library != "QSYS" ||
            descriptor.Name != "QCMDEXC" || descriptor.Attribute != Attribute || descriptor.Source != QcmdexcSource)
            throw new CpfException("IPC0136", "Invalid built-in program adapter.");
    }
}
