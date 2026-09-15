using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;

namespace Ipc.Dsp;

public sealed class DisplayFileStore(SqliteConnectionFactory factory, Func<string, string, string?>? messageResolver = null)
{
    public const string Attribute = "DSPF";
    private const string DefinitionKey = "ipc.dspf.definition.v1";
    public PanelDefinition Create(string library, string name, string source, string? description = null, int ccsid = 37, bool replace = false)
    {
        if (!Ipc.Core.Text.CodePage.IsSupported(ccsid)) throw new CpfException("IPC0127", "Unsupported display-file CCSID.");
        var definition = new DisplayDdsCompiler(messageResolver).Compile(name, source, library + "/" + name);
        var objects = new SqliteObjectStore(factory);
        if (replace && objects.Exists(library, name, ObjectType.File))
        {
            var existing = objects.GetRequired(library, name, ObjectType.File);
            if (existing.Attribute != Attribute) throw new CpfException("CPF4131", "Only an existing display file may be replaced.");
            existing.Source = source; existing.Ccsid = ccsid; existing.Description = description ?? existing.Description;
            var attributes = new Dictionary<string, string>(existing.ExtendedAttributes ?? new Dictionary<string, string>());
            attributes.Remove("ipc.signature"); attributes[DefinitionKey] = definition.ToJson(); existing.ExtendedAttributes = attributes;
            objects.Update(existing); return definition;
        }
        try { objects.Create(new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.File,
            Attribute = Attribute, Source = source, Description = description, Ccsid = ccsid, Owner = OperationIdentity.Current?.Principal ?? "QSECOFR",
            ExtendedAttributes = new Dictionary<string, string> { [DefinitionKey] = definition.ToJson() } }); }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode == 1555 || error.SqliteExtendedErrorCode == 2067)
        { throw new CpfException("CPF7302", "A file with this name already exists."); }
        return definition;
    }
    public PanelDefinition Load(string library, string name)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.File);
        if (descriptor.Attribute != Attribute || descriptor.Source is null || descriptor.ExtendedAttributes?.GetValueOrDefault(DefinitionKey) is not { } json)
            throw new CpfException("CPF4131", "Object is not a compiled display file.");
        var persisted = PanelDefinition.FromJson(json);
        var bindings = persisted.Records.SelectMany(r => r.Fields).SelectMany(f => f.Keywords).Where(k => k.Name == "MSGCON")
            .GroupBy(k => (Id: k.Arguments[1], File: k.Arguments[2])).ToDictionary(g => g.Key, g => g.Select(k => k.BoundText).Distinct().Single());
        var compiled = new DisplayDdsCompiler((id, file) => bindings.GetValueOrDefault((id, file))).Compile(persisted.Name, descriptor.Source, library + "/" + name);
        if (compiled.ToJson() != persisted.ToJson()) throw new CpfException("IPC0127", "Display definition and DDS source disagree; recompile the object.");
        // Object renaming preserves the original compiled record/field contract.
        return persisted;
    }
}
