using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Sqlite;

namespace Ipc.Dsp;

public sealed class HelpPanelStore(SqliteConnectionFactory factory, ObjectSigningService? signing = null)
{
    private const string DefinitionKey = "ipc.pnlgrp.definition.v1";
    public HelpDefinition Create(string library, string name, string source, string? description = null, int ccsid = 37)
    {
        if (!Ipc.Core.Text.CodePage.IsSupported(ccsid)) throw new CpfException("IPC0127", "Unsupported panel-group CCSID.");
        var definition = new UimCompiler().Compile(name, source, library + "/" + name);
        try
        {
            new SqliteObjectStore(factory).Create(new ObjectDescriptor { Key = new(library, name), ObjectType = ObjectType.PanelGroup, Attribute = "UIM",
                Source = source, Description = description, Ccsid = ccsid, Owner = OperationIdentity.Current?.Principal ?? "QSECOFR",
                ExtendedAttributes = new Dictionary<string, string> { [DefinitionKey] = definition.ToJson() } });
        }
        catch (Microsoft.Data.Sqlite.SqliteException error) when (error.SqliteExtendedErrorCode is 1555 or 2067)
        { throw new CpfException("CPF7302", "Panel group already exists."); }
        return definition;
    }
    public HelpDefinition Load(string library, string name)
    {
        var descriptor = new SqliteObjectStore(factory).GetRequired(library, name, ObjectType.PanelGroup);
        (signing ?? new ObjectSigningService(factory, new ContentTrustService(factory, new CertificateService(factory)))).RequireExecutable(descriptor);
        if (descriptor.Attribute != "UIM" || descriptor.Source is null || descriptor.ExtendedAttributes?.GetValueOrDefault(DefinitionKey) is not { } json)
            throw new CpfException("IPC0127", "Object is not a compiled UIM panel group.");
        try
        {
            var persisted = HelpDefinition.FromJson(json);
            var compiled = new UimCompiler().Compile(persisted.Name, descriptor.Source, library + "/" + name);
            if (compiled.ToJson() != persisted.ToJson()) throw new InvalidDataException("Compiled help and source disagree.");
            return compiled;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidDataException or UimCompileException or ArgumentException)
        { throw new CpfException("IPC0127", "Invalid panel group; recompile its source. " + error.Message); }
    }
}
