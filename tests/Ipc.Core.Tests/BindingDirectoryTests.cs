using System.Text.Json.Nodes;
using Ipc.Cl.Commands;
using Ipc.Console.Session;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services;
using Ipc.Services.Work;
using Ipc.Session;

namespace Ipc.Core.Tests;

public sealed class BindingDirectoryTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly BindingDirectoryStore _store;
    public BindingDirectoryTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "DirectoryFixture45"); _store = new(_system.Connections);
    }
    private CommandResult Command(string text) => new CommandService(_system).Execute(text);
    private static void Ok(CommandResult result) => Assert.False(result.IsError, result.Message);
    private void Create(string options = "") => Ok(Command("CRTBNDDIR BNDDIR(QGPL/BINDINGS) " + options));
    private IReadOnlyList<BindingDirectoryEntry> Entries() => _store.Read("QGPL", "BINDINGS");
    private ExecutionSession Session(string user = "QSECOFR") => new(_system, _system.Jobs.CreateInteractive(user), CancellationToken.None);
    [Fact]
    public void Native_commands_create_display_and_delete_ordered_object_payloads()
    {
        Create("TEXT('Native directory') AUT(*USE)"); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE) (SECOND *SRVPGM))"));
        Assert.Equal(["FIRST", "SECOND"], Entries().Select(e => e.Name)); Assert.Equal("*LIBL", Entries()[1].Library);
        var result = Command("DSPBNDDIR BNDDIR(BINDINGS)"); Ok(result); Assert.NotNull(result.Listing); Assert.Equal(2, result.Listing.Count); Assert.Contains("1: QGPL/FIRST *MODULE", result.Listing[0]);
        Ok(Command("DLTBNDDIR BNDDIR(BINDINGS)")); Assert.False(_system.Objects.Exists("QGPL", "BINDINGS", ObjectType.BindingDirectory));
    }
    [Theory]
    [InlineData("*FIRST", "FIRST SECOND THIRD")]
    [InlineData("*LAST", "SECOND THIRD FIRST")]
    [InlineData("*BEFORE QGPL/THIRD *MODULE", "SECOND FIRST THIRD")]
    [InlineData("*AFTER QGPL/SECOND *MODULE", "SECOND FIRST THIRD")]
    [InlineData("*REPLACE QGPL/SECOND *MODULE", "FIRST THIRD")]
    public void Positions_keep_native_entry_order(string position, string expected)
    {
        Create(); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/SECOND *MODULE) (QGPL/THIRD *MODULE))"));
        Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE)) POSITION(" + position + ")"));
        Assert.Equal(expected.Split(' '), Entries().Select(e => e.Name));
    }
    [Fact]
    public void Generic_selectors_remove_matching_names_libraries_and_types()
    {
        Create(); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/PARTONE *MODULE) (QGPL/PARTTWO *SRVPGM) (OTHER/PARTONE *MODULE) (KEEP *SRVPGM))"));
        Ok(Command("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/PART* *MODULE))")); Assert.Equal(3, Entries().Count);
        Ok(Command("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((*ALL/PART* *ALL))")); Assert.Equal("KEEP", Assert.Single(Entries()).Name);
        Ok(Command("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((*ALL/*ALL *ALL))")); Assert.Empty(Entries());
    }
    [Theory]
    [InlineData("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/SECOND *MODULE) (QGPL/FIRST *MODULE))")]
    [InlineData("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/SECOND *MODULE)) POSITION(*BEFORE QGPL/MISSING *MODULE)")]
    [InlineData("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/SECOND *MODULE) (QGPL/THIRD *MODULE)) POSITION(*REPLACE QGPL/FIRST *MODULE)")]
    [InlineData("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE) (QGPL/MISSING *MODULE))")]
    public void A_failed_batch_leaves_payload_and_timestamp_unchanged(string command)
    {
        Create(); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))"));
        var before = _system.Objects.GetRequired("QGPL", "BINDINGS", ObjectType.BindingDirectory); Assert.True(Command(command).IsError);
        var after = _system.Objects.GetRequired("QGPL", "BINDINGS", ObjectType.BindingDirectory); Assert.Equal(before.Source, after.Source); Assert.Equal(before.Changed, after.Changed);
    }
    [Theory]
    [InlineData("OBJ((QGPL/FIRST *FILE))")]
    [InlineData("OBJ((QGPL/FIRST *SRVPGM *SAME))")]
    [InlineData("OBJ((QGPL/FIRST *MODULE *IMMED EXTRA))")]
    [InlineData("OBJ((QGPL/BAD-NAME *MODULE))")]
    [InlineData("OBJ((QGPL/FIRST *MODULE)) POSITION(*INVALID)")]
    [InlineData("OBJ((QGPL/FIRST *MODULE)) POSITION(*FIRST QGPL/OTHER *MODULE)")]
    [InlineData("OBJ((QGPL/FIRST *MODULE)) POSITION(*AFTER)")]
    [InlineData("")]
    public void Invalid_entries_and_positions_reject_before_mutation(string options)
    {
        Create(); Assert.True(Command("ADDBNDDIRE BNDDIR(BINDINGS) " + options).IsError); Assert.Empty(Entries());
    }
    [Theory]
    [InlineData("CRTBNDDIR")] [InlineData("DLTBNDDIR")] [InlineData("ADDBNDDIRE")] [InlineData("RMVBNDDIRE")] [InlineData("DSPBNDDIR")]
    public void Directory_commands_require_their_directory_operand(string command) => Assert.True(Command(command).IsError);
    [Theory]
    [InlineData("version")] [InlineData("unknown")] [InlineData("duplicate")] [InlineData("case")] [InlineData("null")]
    [InlineData("many")] [InlineData("entry-null")] [InlineData("entry-unknown")] [InlineData("entry-case")] [InlineData("identity")]
    public void Strict_images_reject_malformed_saved_directory_data(string defect)
    {
        Create(); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))"));
        var descriptor = _system.Objects.GetRequired("QGPL", "BINDINGS", ObjectType.BindingDirectory); var root = JsonNode.Parse(descriptor.Source!)!.AsObject();
        switch (defect)
        {
            case "version": root["version"] = 2; break;
            case "unknown": root["extra"] = true; break;
            case "case": root["Version"] = 1; break;
            case "null": root["entries"] = null; break;
            case "many": while (root["entries"]!.AsArray().Count < 257) root["entries"]!.AsArray().Add(root["entries"]![0]!.DeepClone()); break;
            case "entry-null": root["entries"]![0] = null; break;
            case "entry-unknown": root["entries"]![0]!["extra"] = true; break;
            case "entry-case": root["entries"]![0]!["Name"] = "FIRST"; break;
            case "identity": root["entries"]![0]!["library"] = "qgpl"; break;
        }
        descriptor.Source = defect == "duplicate" ? root.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal) : root.ToJsonString();
        _system.Objects.Update(descriptor); Assert.True(Command("DSPBNDDIR BNDDIR(BINDINGS)").IsError);
    }
    [Fact]
    public void Entry_quotas_and_duplicates_are_checked_atomically()
    {
        Create(); _store.Add("QGPL", "BINDINGS", Enumerable.Range(1, 256).Select(i => new BindingDirectoryEntry("QGPL", "M" + i, ObjectType.Module)).ToArray());
        Assert.Equal(256, Entries().Count); Assert.Throws<InvalidDataException>(() => _store.Add("QGPL", "BINDINGS", [new("QGPL", "EXTRA", ObjectType.Module)])); Assert.Equal(256, Entries().Count);
    }
    [Fact]
    public void Add_and_remove_require_operational_data_authorities_without_requiring_read_or_management()
    {
        Create("AUT(*EXCLUDE)"); _system.Security.Profiles.Create(new UserProfile { Name = "DIRUSER" }); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("DIRUSER"), "DirectoryUser45");
        _system.Security.Authority.Grant("QSYS", "QGPL", ObjectType.Library, "DIRUSER", Authorities.UseBits);
        using var session = Session("DIRUSER"); _system.Security.Authority.Grant("QGPL", "BINDINGS", ObjectType.BindingDirectory, "DIRUSER", AuthorityBit.ObjectOperate | AuthorityBit.Add);
        Ok(session.Execute("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))")); Assert.True(session.Execute("DSPBNDDIR BNDDIR(BINDINGS)").IsError); Assert.True(session.Execute("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))").IsError);
        _system.Security.Authority.Grant("QGPL", "BINDINGS", ObjectType.BindingDirectory, "DIRUSER", AuthorityBit.ObjectOperate | AuthorityBit.Delete);
        Ok(session.Execute("RMVBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))")); Assert.True(session.Execute("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))").IsError);
        Assert.Empty(Entries());
    }
    [Fact]
    public void Authorization_lists_and_library_create_authority_attach_during_creation()
    {
        _system.Security.Authority.AddAuthLMember("DIRAUTH", "QUSER", Authorities.UseBits); Create("AUT(DIRAUTH)");
        var descriptor = _system.Objects.GetRequired("QGPL", "BINDINGS", ObjectType.BindingDirectory); Assert.True(descriptor.UseAuthorizationListPublicAuthority);
        Ok(Command("DSPBNDDIR BNDDIR(BINDINGS)"));
        _store.Create("QGPL", "INHERIT"); Assert.Equal(Authorities.ChangeBits, _system.Objects.GetRequired("QGPL", "INHERIT", ObjectType.BindingDirectory).PublicAuthority);
    }
    [Fact]
    public void Current_library_and_QTEMP_directories_follow_the_jobs_namespace()
    {
        using var first = Session(); using var second = Session(); Ok(first.Execute("CRTLIB LIB(DIRLIB)")); Ok(first.Execute("CHGCURLIB CURLIB(DIRLIB)"));
        Ok(first.Execute("CRTBNDDIR BNDDIR(CURRENT)")); Assert.True(_system.Objects.Exists("DIRLIB", "CURRENT", ObjectType.BindingDirectory));
        Ok(first.Execute("CRTBNDDIR BNDDIR(QTEMP/PRIVATE)")); Ok(first.Execute("ADDBNDDIRE BNDDIR(QTEMP/PRIVATE) OBJ((QGPL/FIRST *MODULE))"));
        Ok(first.Execute("DSPBNDDIR BNDDIR(QTEMP/PRIVATE)")); Assert.True(second.Execute("DSPBNDDIR BNDDIR(QTEMP/PRIVATE)").IsError);
    }
    [Fact]
    public void Object_copy_and_rename_keep_the_ordered_directory_payload()
    {
        Create(); Ok(Command("ADDBNDDIRE BNDDIR(BINDINGS) OBJ((QGPL/FIRST *MODULE))"));
        Ok(Command("CRTDUPOBJ OBJ(BINDINGS) FROMLIB(QGPL) OBJTYPE(*BNDDIR) TOLIB(QGPL) NEWOBJ(COPY)"));
        Ok(Command("RNMOBJ OBJ(QGPL/COPY) OBJTYPE(*BNDDIR) NEWOBJ(RENAMED)")); Assert.Equal("FIRST", Assert.Single(_store.Read("QGPL", "RENAMED")).Name);
        Assert.Equal("FIRST", Assert.Single(Entries()).Name);
    }
    [Fact]
    public void Concurrent_directory_additions_read_the_latest_payload_inside_the_write_transaction()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-binding-" + Guid.NewGuid().ToString("N")); global::System.IO.Directory.CreateDirectory(directory);
        try
        {
            using var system = IpcSystem.Create(Path.Combine(directory, "system.db")); system.Start(); var store = new BindingDirectoryStore(system.Connections); store.Create("QGPL", "BINDINGS");
            Parallel.For(1, 13, i => store.Add("QGPL", "BINDINGS", [new("QGPL", "M" + i, ObjectType.Module)]));
            Assert.Equal(12, store.Read("QGPL", "BINDINGS").Count);
        }
        finally { global::System.IO.Directory.Delete(directory, recursive: true); }
    }
    public void Dispose() => _system.Dispose();
}
