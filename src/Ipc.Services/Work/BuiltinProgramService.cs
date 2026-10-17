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
    private static readonly IReadOnlyDictionary<string, string> Programs = new Dictionary<string, string> {
        ["QCMDEXC"] = "Execute command API", ["QSNDDTAQ"] = "Send data queue API",
        ["QRCVDTAQ"] = "Receive data queue API", ["QCLRDTAQ"] = "Clear data queue API" };
    private static string Manifest(string name) => "{\"api\":\"" + name + "\",\"version\":1}";

    public static void SeedDefaults(SqliteConnectionFactory factory)
    {
        new ServiceAuthorization(factory).RequireSpecial(SpecialAuthority.SecurityAdministrator);
        var objects = new SqliteObjectStore(factory);
        // Existing objects, including signed or locally replaced programs, are never overwritten.
        foreach (var (name, text) in Programs)
            if (objects.GetForAuthorization("QSYS", name, ObjectType.Program) is null)
                objects.Create(new ObjectDescriptor { Key = new("QSYS", name), ObjectType = ObjectType.Program,
                    Owner = "QSYS", Attribute = Attribute, Source = Manifest(name), Description = text });
    }

    public static void Validate(ObjectDescriptor descriptor)
    {
        if (descriptor.ObjectType != ObjectType.Program || descriptor.Library != "QSYS" ||
            !Programs.ContainsKey(descriptor.Name) || descriptor.Attribute != Attribute || descriptor.Source != Manifest(descriptor.Name))
            throw new CpfException("IPC0136", "Invalid built-in program adapter.");
    }
}
