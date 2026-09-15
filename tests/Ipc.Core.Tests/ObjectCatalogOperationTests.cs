using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Microsoft.Data.Sqlite;

namespace Ipc.Core.Tests;

public sealed class ObjectCatalogOperationTests : IDisposable
{
    private readonly IpcSystem _system;
    private readonly SqliteFileStore _files;
    public ObjectCatalogOperationTests()
    {
        _system = IpcSystem.Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), ":memory:");
        _system.Start();
        _files = new SqliteFileStore(_system.Connections, _system.Objects);
    }

    [Fact]
    public void Renaming_a_library_moves_member_tables_and_its_object_namespace_atomically()
    {
        _system.Libraries.CreateLibrary("OLDLIB");
        _files.CreateSourceFile("OLDLIB", "SRC");
        _system.ObjectOperations.Relocate(new QualifiedName("QSYS", "OLDLIB"), ObjectType.Library, new QualifiedName("QSYS", "NEWLIB"));
        Assert.False(_system.Libraries.LibraryExists("OLDLIB"));
        Assert.True(_system.Libraries.LibraryExists("NEWLIB"));
        Assert.True(_files.FileExists("NEWLIB", "SRC"));
        Assert.Equal(0, _files.RowCount("NEWLIB", "SRC", "SRC"));
        _system.Jobs.CreateInteractive("USER", currentLibrary: "NEWLIB");
        Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new QualifiedName("QSYS", "NEWLIB"), ObjectType.Library, new QualifiedName("QSYS", "THIRDLIB")));
        Assert.True(_system.Libraries.LibraryExists("NEWLIB"));
        Assert.False(_system.Libraries.LibraryExists("THIRDLIB"));
    }

    [Fact]
    public void Copy_and_move_preserve_members_metadata_and_type_identity()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        _files.AddMember("QGPL", "SOURCE", "SECOND");
        _files.Insert("QGPL", "SOURCE", "SOURCE", "SOURCE", new Dictionary<string, object?> { ["SRCDTA"] = "original" });
        var descriptor = _system.Objects.GetRequired("QGPL", "SOURCE", ObjectType.File);
        descriptor.Owner = "OWNER";
        descriptor.Signature = new ObjectSignature("TEST", "K", "H", "V", DateTimeOffset.UtcNow);
        _system.Objects.Update(descriptor);
        _system.Objects.Create(new ObjectDescriptor { Key = descriptor.Key, ObjectType = ObjectType.Program, Source = "program" });
        _system.ObjectOperations.Relocate(descriptor.Key, ObjectType.File, new QualifiedName("QGPL", "COPY"), copy: true);
        Assert.Equal(new[] { "SECOND", "SOURCE" }, _files.ListMembers("QGPL", "COPY"));
        Assert.Equal(1, _files.RowCount("QGPL", "COPY", "SOURCE"));
        Assert.Equal("COPY", _files.GetDefinition("QGPL", "COPY")!.Name);
        Assert.Null(_system.Objects.GetRequired("QGPL", "COPY", ObjectType.File).Signature);
        Assert.NotNull(_system.Objects.GetRequired("QGPL", "SOURCE", ObjectType.File).Signature);
        _system.Libraries.CreateLibrary("DEST");
        _system.ObjectOperations.Relocate(descriptor.Key, ObjectType.File, new QualifiedName("DEST", "MOVED"));
        Assert.False(_files.FileExists("QGPL", "SOURCE"));
        Assert.Equal(1, _files.RowCount("DEST", "MOVED", "SOURCE"));
        Assert.Equal("OWNER", _system.Objects.GetRequired("DEST", "MOVED", ObjectType.File).Owner);
        Assert.Equal("program", _system.Objects.GetRequired("QGPL", "SOURCE", ObjectType.Program).Source);
    }

    [Fact]
    public void Failed_member_copy_rolls_back_all_new_tables_and_catalog_rows()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        _files.AddMember("QGPL", "SOURCE", "ZZLAST");
        using var connection = _system.Connections.Open();
        using var conflict = connection.CreateCommand();
        conflict.CommandText = "CREATE TABLE \"QGPL.COPY.ZZLAST\" (existing TEXT)";
        conflict.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => _system.ObjectOperations.Relocate(new QualifiedName("QGPL", "SOURCE"), ObjectType.File, new QualifiedName("QGPL", "COPY"), copy: true));
        Assert.False(_files.FileExists("QGPL", "COPY"));
        conflict.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='QGPL.COPY.SOURCE'";
        Assert.Equal(0L, conflict.ExecuteScalar());
        Assert.Equal(2, _files.ListMembers("QGPL", "SOURCE").Count);
    }

    [Fact]
    public void Registered_dependencies_follow_rename_and_prevent_partial_file_deletion()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        var source = new QualifiedName("QGPL", "SOURCE");
        var dependent = new QualifiedName("QGPL", "READER");
        _system.Objects.Create(new ObjectDescriptor { Key = dependent, ObjectType = ObjectType.Program });
        _system.ObjectOperations.AddDependency(dependent, ObjectType.Program, source, ObjectType.File);
        _system.ObjectOperations.Relocate(source, ObjectType.File, new QualifiedName("QGPL", "RENAMED"));
        Assert.Throws<CpfException>(() => _files.DeleteFile("QGPL", "RENAMED"));
        Assert.True(_files.MemberExists("QGPL", "RENAMED", "SOURCE"));
        _system.Objects.Delete("QGPL", "READER", ObjectType.Program);
        _files.DeleteFile("QGPL", "RENAMED");
        Assert.False(_files.FileExists("QGPL", "RENAMED"));
        Assert.False(_files.MemberExists("QGPL", "RENAMED", "SOURCE"));
    }

    [Fact]
    public void Failed_file_creation_leaves_no_object_definition_or_member()
    {
        var format = new RecordFormat { Name = "REC", Fields = new()
        {
            new FieldSpec { Name = "DUP", Length = 5, Type = FieldType.Alpha },
            new FieldSpec { Name = "DUP", Length = 5, Type = FieldType.Alpha },
        } };
        var definition = new FileDefinition { Name = "BROKEN", Attribute = FileAttribute.Physical, Formats = new() { format } };
        Assert.Throws<SqliteException>(() => _files.CreatePhysicalFile("QGPL", "BROKEN", definition, ""));
        Assert.False(_files.FileExists("QGPL", "BROKEN"));
        Assert.Null(_files.GetDefinition("QGPL", "BROKEN"));
        Assert.Empty(_files.ListMembers("QGPL", "BROKEN"));
    }

    [Fact]
    public void Copy_preserves_constraints_generated_columns_sequences_and_trigger_behavior()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        Sql("""
            DROP TABLE "QGPL.SOURCE.SOURCE";
            CREATE TABLE "QGPL.SOURCE.SOURCE" (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                parent INTEGER REFERENCES "QGPL.SOURCE.SOURCE"(id),
                label TEXT NOT NULL UNIQUE CHECK(length(label)>0),
                derived TEXT GENERATED ALWAYS AS (label || ':QGPL.SOURCE.SOURCE') STORED
            );
            CREATE UNIQUE INDEX "index with spaces" ON "QGPL.SOURCE.SOURCE" (lower(label)) WHERE parent IS NOT NULL;
            CREATE TABLE audit_log (message TEXT);
            CREATE TRIGGER "trigger with spaces" AFTER INSERT ON "QGPL.SOURCE.SOURCE"
            BEGIN
                INSERT INTO audit_log VALUES ('QGPL.SOURCE.SOURCE');
                UPDATE "QGPL.SOURCE.SOURCE" SET label=upper(label) WHERE id=new.id;
            END;
            INSERT INTO "QGPL.SOURCE.SOURCE" (id,label) VALUES (7,'root');
            INSERT INTO "QGPL.SOURCE.SOURCE" (id,parent,label) VALUES (12,7,'child');
            INSERT INTO "QGPL.SOURCE.SOURCE" (id,label) VALUES (99,'deleted');
            DELETE FROM "QGPL.SOURCE.SOURCE" WHERE id=99;
            CREATE VIEW original_view AS SELECT * FROM "QGPL.SOURCE.SOURCE";
            CREATE TABLE external_child (id INTEGER REFERENCES "QGPL.SOURCE.SOURCE"(id));
            INSERT INTO external_child VALUES (7);
            """);
        _system.ObjectOperations.Relocate(new("QGPL", "SOURCE"), ObjectType.File, new("QGPL", "COPY"), copy: true);
        Assert.Equal(3L, Scalar("SELECT count(*) FROM audit_log"));
        Assert.Equal("CHILD:QGPL.SOURCE.SOURCE", Scalar("SELECT derived FROM \"QGPL.COPY.SOURCE\" WHERE id=12"));
        Assert.Equal("QGPL.COPY.SOURCE", Scalar("SELECT \"table\" FROM pragma_foreign_key_list('QGPL.COPY.SOURCE')"));
        Assert.Equal("QGPL.SOURCE.SOURCE", Scalar("SELECT \"table\" FROM pragma_foreign_key_list('external_child')"));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM original_view"));
        Assert.Throws<SqliteException>(() => Sql("INSERT INTO \"QGPL.COPY.SOURCE\" (label,parent) VALUES ('orphan',500)"));
        Sql("INSERT INTO \"QGPL.COPY.SOURCE\" (label,parent) VALUES ('new',7)");
        Assert.Equal(100L, Scalar("SELECT id FROM \"QGPL.COPY.SOURCE\" WHERE label='NEW'"));
        Assert.Equal(4L, Scalar("SELECT count(*) FROM audit_log"));
        Assert.Equal("QGPL.SOURCE.SOURCE", Scalar("SELECT message FROM audit_log ORDER BY rowid DESC LIMIT 1"));
        Assert.Equal(2L, Scalar("SELECT count(*) FROM original_view"));
        Assert.Throws<SqliteException>(() => Sql("INSERT INTO \"QGPL.COPY.SOURCE\" (label,parent) VALUES ('child',7)"));
        Assert.Throws<SqliteException>(() => Sql("DELETE FROM \"QGPL.COPY.SOURCE\" WHERE id=7"));
        Assert.Equal("ok", Scalar("PRAGMA integrity_check"));
    }

    [Fact]
    public void Copy_preserves_sparse_record_numbers_without_rowid_and_cross_member_foreign_keys()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        _files.AddMember("QGPL", "SOURCE", "ZPARENT");
        Sql("""
            DROP TABLE "QGPL.SOURCE.SOURCE";
            DROP TABLE "QGPL.SOURCE.ZPARENT";
            CREATE TABLE "QGPL.SOURCE.ZPARENT" (id TEXT PRIMARY KEY) WITHOUT ROWID;
            CREATE TABLE "QGPL.SOURCE.SOURCE" (parent TEXT REFERENCES "QGPL.SOURCE.ZPARENT"(id), value TEXT);
            INSERT INTO "QGPL.SOURCE.ZPARENT" VALUES ('A');
            INSERT INTO "QGPL.SOURCE.SOURCE" (rowid,parent,value) VALUES (42,'A','row');
            """);
        _system.ObjectOperations.Relocate(new("QGPL", "SOURCE"), ObjectType.File, new("QGPL", "COPY"), copy: true);
        Assert.Equal(42L, Scalar("SELECT rowid FROM \"QGPL.COPY.SOURCE\""));
        Assert.Equal("A", Scalar("SELECT id FROM \"QGPL.COPY.ZPARENT\""));
        Assert.Equal("QGPL.COPY.ZPARENT", Scalar("SELECT \"table\" FROM pragma_foreign_key_list('QGPL.COPY.SOURCE')"));
        Sql("DELETE FROM \"QGPL.COPY.SOURCE\"; DELETE FROM \"QGPL.COPY.ZPARENT\"");
        Assert.Equal(1L, Scalar("SELECT count(*) FROM \"QGPL.SOURCE.ZPARENT\""));
    }

    [Fact]
    public void Failed_index_copy_restores_source_schema_and_removes_partial_payload()
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        Sql("""
            CREATE INDEX original_index ON "QGPL.SOURCE.SOURCE" (SRCDTA);
            CREATE INDEX "QGPL.COPY.SOURCE.index.original_index" ON "QGPL.SOURCE.SOURCE" (SRCDAT);
            CREATE VIEW original_view AS SELECT * FROM "QGPL.SOURCE.SOURCE";
            """);
        Assert.Throws<SqliteException>(() => _system.ObjectOperations.Relocate(new("QGPL", "SOURCE"), ObjectType.File, new("QGPL", "COPY"), copy: true));
        Assert.False(_files.FileExists("QGPL", "COPY"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sqlite_schema WHERE name='QGPL.COPY.SOURCE'"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM original_view"));
        Assert.Equal("QGPL.SOURCE.SOURCE", Scalar("SELECT tbl_name FROM sqlite_schema WHERE name='original_index'"));
    }

    [Theory]
    [InlineData("CREATE TABLE child (id INTEGER REFERENCES \"QGPL.SOURCE.SOURCE\"(id) ON DELETE CASCADE); INSERT INTO child VALUES (7)")]
    [InlineData("CREATE VIEW child AS SELECT * FROM \"QGPL.SOURCE.SOURCE\"")]
    [InlineData("CREATE TABLE child (id INTEGER); CREATE TRIGGER reader AFTER INSERT ON child BEGIN UPDATE \"QGPL.SOURCE.SOURCE\" SET id=id; END")]
    public void Deleting_file_rejects_external_sql_dependencies_without_cascading_or_breaking_views(string dependent)
    {
        _files.CreateSourceFile("QGPL", "SOURCE");
        Sql("DROP TABLE \"QGPL.SOURCE.SOURCE\"; CREATE TABLE \"QGPL.SOURCE.SOURCE\" (id INTEGER PRIMARY KEY); INSERT INTO \"QGPL.SOURCE.SOURCE\" VALUES (7)");
        Sql(dependent);
        var error = Assert.Throws<CpfException>(() => _files.DeleteFile("QGPL", "SOURCE"));
        Assert.Contains("external SQL dependents", error.Message);
        Assert.True(_files.FileExists("QGPL", "SOURCE"));
        Assert.Equal(7L, Scalar("SELECT id FROM \"QGPL.SOURCE.SOURCE\""));
        Assert.Equal("ok", Scalar("PRAGMA integrity_check"));
        _system.ObjectOperations.Relocate(new("QGPL", "SOURCE"), ObjectType.File, new("QGPL", "MOVED"));
        Assert.Throws<CpfException>(() => _files.DeleteFile("QGPL", "MOVED"));
        Assert.Equal(7L, Scalar("SELECT id FROM \"QGPL.MOVED.SOURCE\""));
    }

    [Fact]
    public void Menu_updates_and_failed_registration_keep_descriptor_and_payload_consistent()
    {
        _system.Menus.Register(new Ipc.Core.Menu.ApplicationMenu { Library = "QGPL", Name = "CUSTOM", Title = "Original" }, "OWNER");
        _system.Menus.Register(new Ipc.Core.Menu.ApplicationMenu { Library = "QGPL", Name = "CUSTOM", Title = "Changed" }, "EDITOR");
        Assert.Equal("OWNER", _system.Objects.GetRequired("QGPL", "CUSTOM", ObjectType.Menu).Owner);
        Assert.Equal("Changed", _system.Objects.GetRequired("QGPL", "CUSTOM", ObjectType.Menu).Description);
        var broken = new Ipc.Core.Menu.ApplicationMenu
        {
            Library = "QGPL", Name = "BROKEN", Title = "Broken",
            Options = new[] { new Ipc.Core.Menu.MenuOption { Number = "1", Text = "Test", Target = null! } },
        };
        Assert.ThrowsAny<Exception>(() => _system.Menus.Register(broken));
        Assert.False(_system.Objects.Exists("QGPL", "BROKEN", ObjectType.Menu));
        Assert.Null(_system.Menus.TryGet("BROKEN", "QGPL"));
    }

    [Fact]
    public void Qualified_menu_references_follow_relocation_and_block_deletion()
    {
        _system.Menus.Register(new Ipc.Core.Menu.ApplicationMenu { Library = "QGPL", Name = "TARGET" });
        _system.Menus.Register(new Ipc.Core.Menu.ApplicationMenu
        {
            Library = "QGPL", Name = "PARENT",
            Options = new[] { new Ipc.Core.Menu.MenuOption { Number = "1", Text = "Child", Target = "QGPL/TARGET" } },
        });
        _system.ObjectOperations.Relocate(new("QGPL", "TARGET"), ObjectType.Menu, new("QGPL", "RENAMED"));
        Assert.Equal("QGPL/RENAMED", _system.Menus.Get("PARENT", "QGPL").Options[0].Target);
        Assert.Throws<CpfException>(() => _system.Objects.Delete("QGPL", "RENAMED", ObjectType.Menu));
        _system.Objects.Delete("QGPL", "PARENT", ObjectType.Menu);
        _system.Objects.Delete("QGPL", "RENAMED", ObjectType.Menu);
        Assert.Null(_system.Menus.TryGet("RENAMED", "QGPL"));
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("system")]
    [InlineData("job")]
    public void Configured_library_references_block_rename_and_delete(string reference)
    {
        _system.Libraries.CreateLibrary("REFERENCED");
        if (reference == "profile")
        {
            var profile = _system.Security.Profiles.Get("QUSER");
            profile.InitialCurrentLibrary = "REFERENCED";
            _system.Security.Profiles.Update(profile);
        }
        else if (reference == "system") _system.SetSystemValue("QUSRLIBL", "QGPL REFERENCED");
        else _system.Jobs.CreateInteractive("QUSER", currentLibrary: "REFERENCED");
        Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(new("QSYS", "REFERENCED"), ObjectType.Library, new("QSYS", "RENAMED")));
        Assert.Throws<CpfException>(() => _system.Objects.Delete("QSYS", "REFERENCED", ObjectType.Library));
        Assert.True(_system.Libraries.LibraryExists("REFERENCED"));
        Assert.False(_system.Libraries.LibraryExists("RENAMED"));
    }

    [Fact]
    public void Profiles_share_type_qualified_metadata_without_exposing_credentials()
    {
        var profile = new Ipc.Core.Security.UserProfile { Name = "PERSON", Description = "Initial", InitialCurrentLibrary = "QGPL" };
        _system.Security.Profiles.Create(profile);
        _system.Security.Profiles.SetPassword(profile, "PASSWORD1");
        var descriptor = _system.Objects.GetRequired("QSYS", "PERSON", ObjectType.UserProfile);
        Assert.Equal("Initial", descriptor.Description);
        Assert.Null(descriptor.Source);
        Assert.Null(descriptor.ExtendedAttributes);
        descriptor.Description = "Updated";
        descriptor.Owner = "QSYS";
        _system.Objects.Update(descriptor);
        Assert.Equal("Updated", _system.Security.Profiles.Get("PERSON").Description);
        Assert.Equal("QSYS", _system.Security.Profiles.Get("PERSON").Owner);
        Assert.True(_system.Security.Profiles.VerifyPassword("PERSON", "PASSWORD1"));
        Assert.Throws<CpfException>(() => _system.ObjectOperations.Relocate(descriptor.Key, ObjectType.UserProfile, new("QSYS", "CLONE"), copy: true));
        Assert.False(_system.Security.Profiles.Exists("CLONE"));
        _system.Security.Profiles.Delete("PERSON");
        Assert.False(_system.Objects.Exists("QSYS", "PERSON", ObjectType.UserProfile));
        Assert.False(_system.Security.Profiles.Exists("PERSON"));
        Assert.Throws<CpfException>(() => _system.Security.Profiles.Update(profile));
        Assert.False(_system.Objects.Exists("QSYS", "PERSON", ObjectType.UserProfile));
    }

    [Theory]
    [InlineData("owned")]
    [InlineData("group")]
    [InlineData("job")]
    public void Profile_deletion_rejects_live_references_through_both_service_and_object_operations(string reference)
    {
        _system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = "PERSON" });
        if (reference == "owned") _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "OWNED"), ObjectType = ObjectType.Program, Owner = "PERSON" });
        else if (reference == "group") _system.Security.Profiles.Create(new Ipc.Core.Security.UserProfile { Name = "MEMBER", GroupProfile = "PERSON" });
        else _system.Jobs.CreateInteractive("PERSON");
        Assert.Throws<CpfException>(() => _system.Security.Profiles.Delete("PERSON"));
        Assert.Throws<CpfException>(() => _system.Objects.Delete("QSYS", "PERSON", ObjectType.UserProfile));
        Assert.True(_system.Security.Profiles.Exists("PERSON"));
        Assert.True(_system.Objects.Exists("QSYS", "PERSON", ObjectType.UserProfile));
    }

    [Fact]
    public void Subsystem_copy_and_move_preserve_routing_and_qualified_identity()
    {
        _system.Libraries.CreateLibrary("WORK");
        _system.Libraries.CreateLibrary("OTHER");
        _system.Subsystems.Ensure("WORK/SBS", "Work subsystem", 4);
        _system.Subsystems.Ensure("OTHER/SBS", "Other subsystem", 9);
        var routing = new Ipc.Services.Work.RoutingTable(_system.Connections);
        routing.EnsureEntry("WORK/SBS", 10, "*ANY", "QSYS/QCMD");
        _system.ObjectOperations.Relocate(new("WORK", "SBS"), ObjectType.SubsystemDescription, new("WORK", "COPY"), copy: true);
        Assert.Equal("QSYS/QCMD", routing.Route("WORK/COPY", "ANY"));
        Assert.Equal(4, _system.Subsystems.StatusAll().Single(s => s.Name == "WORK/COPY").MaxActiveJobs);
        _system.ObjectOperations.Relocate(new("WORK", "SBS"), ObjectType.SubsystemDescription, new("OTHER", "MOVED"));
        Assert.Null(routing.Route("WORK/SBS", "ANY"));
        Assert.Equal("QSYS/QCMD", routing.Route("OTHER/MOVED", "ANY"));
        Assert.Equal(9, _system.Subsystems.StatusAll().Single(s => s.Name == "OTHER/SBS").MaxActiveJobs);
        _system.Subsystems.Start("OTHER/MOVED");
        Assert.Throws<CpfException>(() => _system.Objects.Delete("OTHER", "MOVED", ObjectType.SubsystemDescription));
        _system.Subsystems.End("OTHER/MOVED");
        _system.Objects.Delete("OTHER", "MOVED", ObjectType.SubsystemDescription);
        Assert.Null(routing.Route("OTHER/MOVED", "ANY"));
        Assert.DoesNotContain(_system.Subsystems.StatusAll(), s => s.Name == "OTHER/MOVED");
    }

    [Fact]
    public void Job_queues_have_qualified_identity_and_payloads_follow_object_operations()
    {
        _system.Libraries.CreateLibrary("WORK");
        _system.Libraries.CreateLibrary("OTHER");
        var queues = new Ipc.Services.Work.JobQueueStore(_system.Connections);
        queues.Ensure("QUEUE", "WORK", "Work queue");
        queues.Ensure("QUEUE", "OTHER", "Other queue");
        Assert.True(queues.Exists("WORK/QUEUE"));
        Assert.True(queues.Exists("OTHER/QUEUE"));
        _system.ObjectOperations.Relocate(new("WORK", "QUEUE"), ObjectType.JobQueue, new("WORK", "COPY"), copy: true);
        Assert.True(queues.Exists("WORK/COPY"));
        _system.ObjectOperations.Relocate(new("WORK", "QUEUE"), ObjectType.JobQueue, new("OTHER", "MOVED"));
        Assert.False(queues.Exists("WORK/QUEUE"));
        Assert.True(queues.Exists("OTHER/MOVED"));
        _system.Objects.Delete("OTHER", "MOVED", ObjectType.JobQueue);
        Assert.False(queues.Exists("OTHER/MOVED"));
        Assert.True(queues.Exists("OTHER/QUEUE"));
    }

    [Fact]
    public void Batch_claims_distinguish_same_named_subsystems_in_different_libraries()
    {
        _system.Libraries.CreateLibrary("WORK");
        _system.Libraries.CreateLibrary("OTHER");
        _system.Subsystems.Ensure("WORK/SBS", "Work", 1);
        _system.Subsystems.Ensure("OTHER/SBS", "Other", 1);
        _system.Subsystems.Start("WORK/SBS");
        _system.Subsystems.Start("OTHER/SBS");
        _system.Jobs.Submit("ONE", "QUSER", "WORK/SBS", command: "SIGNOFF");
        _system.Jobs.Submit("TWO", "QUSER", "WORK/SBS", command: "SIGNOFF");
        _system.Jobs.Submit("THREE", "QUSER", "OTHER/SBS", command: "SIGNOFF");
        var queue = new Ipc.Services.Work.BatchQueue(_system.Connections, _system.Jobs);
        Assert.Equal("WORK/SBS", queue.ClaimNext()!.Value.Job.Subsystem);
        Assert.Equal("OTHER/SBS", queue.ClaimNext()!.Value.Job.Subsystem);
        Assert.Null(queue.ClaimNext());
    }

    [Fact]
    public void Authorization_list_identity_members_and_attachments_follow_rename_and_copy()
    {
        var authority = _system.Security.Authority;
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "SECURED"), ObjectType = ObjectType.Program, PublicAuthority = AuthorityBit.None });
        authority.AddAuthLMember("ACCESS", "READER", Authorities.UseBits);
        authority.GrantAuthL("QGPL", "SECURED", ObjectType.Program, "ACCESS", Authorities.UseBits);
        Assert.True(_system.Objects.Exists("QSYS", "ACCESS", ObjectType.AuthorizationList));
        _system.ObjectOperations.Relocate(new("QSYS", "ACCESS"), ObjectType.AuthorizationList, new("QSYS", "RENAMED"));
        var user = new Ipc.Core.Security.UserProfile { Name = "READER" };
        Assert.True(authority.CanExecute(user, Array.Empty<string>(), "QGPL", "SECURED", ObjectType.Program, Authorities.UseBits));
        Assert.Throws<CpfException>(() => _system.Objects.Delete("QSYS", "RENAMED", ObjectType.AuthorizationList));
        _system.ObjectOperations.Relocate(new("QSYS", "RENAMED"), ObjectType.AuthorizationList, new("QSYS", "COPY"), copy: true);
        Assert.Equal((long)Authorities.UseBits, Scalar("SELECT bits FROM sys_authl_members WHERE authl='COPY' AND holder='READER'"));
        _system.Objects.Delete("QSYS", "COPY", ObjectType.AuthorizationList);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_authl_members WHERE authl='COPY'"));
        _system.Objects.Delete("QGPL", "SECURED", ObjectType.Program);
        _system.Objects.Delete("QSYS", "RENAMED", ObjectType.AuthorizationList);
        Assert.Throws<ArgumentException>(() => authority.AddAuthLMember("TOOLONGNAME", "READER", Authorities.UseBits));
        Assert.False(_system.Objects.Exists("QSYS", "COPY", ObjectType.AuthorizationList));
    }

    private void Sql(string sql)
    {
        using var connection = _system.Connections.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var connection = _system.Connections.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose() => _system.Dispose();
}
