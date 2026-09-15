using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

/// <summary>Transactional catalog/payload operations. Authority is applied by the caller's service boundary.</summary>
public sealed partial class ObjectCatalogOperations(SqliteConnectionFactory factory)
{
    public void AddDependency(QualifiedName source, string sourceType, QualifiedName target, string targetType)
    {
        var authorization = new Ipc.Services.Security.ServiceAuthorization(factory);
        authorization.RequireObject(source.Library, source.Name.Value, sourceType, AuthorityBit.ObjectManagement);
        authorization.RequireObject(target.Library, target.Name.Value, targetType, AuthorityBit.ObjectReference);
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sys_object_dependencies VALUES ($sl,$sn,$st,$tl,$tn,$tt)
            ON CONFLICT DO NOTHING
            """;
        foreach (var (name, value) in new[] { ("$sl", source.Library), ("$sn", source.Name.Value), ("$st", sourceType),
            ("$tl", target.Library), ("$tn", target.Name.Value), ("$tt", targetType) })
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    public void Delete(QualifiedName source, string type)
    {
        new ObjectDescriptor { Key = source, ObjectType = type }.ValidateIdentity();
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireObject(source.Library, source.Name.Value, type, AuthorityBit.ObjectExist);
        new Ipc.Services.Work.JobLockStore(factory).RequireNoPersistentAllocation(new(source.Library, source.Name.Value, type));
        if (type == ObjectType.UserProfile) new Ipc.Services.Security.ServiceAuthorization(factory).RequireSpecial(Ipc.Core.Security.SpecialAuthority.SecurityAdministrator);
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        DeleteCore(source, type, connection, transaction);
        transaction.Commit();
    }

    private static void DeleteCore(QualifiedName source, string type, SqliteConnection connection, SqliteTransaction transaction)
    {
        var operation = new Operation(connection, transaction, source, source, type);
        if (operation.Number("SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type") == 0) return;
        if (operation.Number("SELECT count(*) FROM sys_object_dependencies WHERE target_lib=$lib AND target_name=$name AND target_type=$type AND NOT(source_lib=$lib AND source_name=$name AND source_type=$type)") > 0)
            throw new CpfException("IPC0201", "Object has registered dependents; remove their dependency first.");
        if (type == ObjectType.Library && operation.Number("SELECT count(*) FROM sys_objects WHERE lib=$name") > 0)
            throw new CpfException("IPC0201", "Library is not empty.");
        if (type == ObjectType.Library) EnsureLibraryAvailable(operation, source.Name.Value);
        if (type == ObjectType.UserProfile) ValidateProfileDeletion(operation, source);
        ValidateWorkOperation(operation, type);
        if (type == ObjectType.AuthorizationList && operation.Number("SELECT count(*) FROM sys_authorities WHERE holder=$name AND is_authl=1") != 0)
            throw new CpfException("IPC0201", "Authorization list is attached to objects; detach it before deletion.");
        if (HasNamedReferences(operation, type))
            throw new CpfException("IPC0201", "Object is referenced by a menu, profile, or routing entry.");
        var tables = operation.Members().Select(member => $"{source.Library}.{source.Name}.{member}").ToArray();
        if (MemberTableDependencies.HasExternalReferences(connection, transaction, tables))
            throw new CpfException("IPC0201", "Member tables have external SQL dependents.");
        if (type == ObjectType.File && operation.Number("SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type AND attribute='*LF'") != 0)
            LogicalPathCatalog.ReleaseFile(connection, transaction, source);
        operation.Run("PRAGMA defer_foreign_keys=ON");
        foreach (var table in tables) operation.Run($"DROP TABLE {Quote(table)}");
        operation.Run("DELETE FROM sys_file_members WHERE lib=$lib AND name=$name AND type=$type");
        operation.Run("DELETE FROM sys_file_defs WHERE lib=$lib AND name=$name AND type=$type");
        operation.Run("DELETE FROM sys_authorities WHERE lib=$lib AND name=$name AND type=$type");
        if (type == ObjectType.Menu)
        {
            operation.Run("DELETE FROM sys_menu_options WHERE library=$lib AND menu=$name");
            operation.Run("DELETE FROM sys_menus WHERE library=$lib AND name=$name");
        }
        operation.Run("DELETE FROM sys_object_dependencies WHERE source_lib=$lib AND source_name=$name AND source_type=$type");
        if (type == ObjectType.SubsystemDescription)
        {
            operation.Run("DELETE FROM sys_routing WHERE subsystem=$workSource");
            operation.Run("DELETE FROM sys_subsystems WHERE library=$lib AND name=$name");
        }
        if (type == ObjectType.JobQueue) operation.Run("DELETE FROM sys_jobqs WHERE library=$lib AND name=$name");
        if (type == ObjectType.AuthorizationList) operation.Run("DELETE FROM sys_authl_members WHERE authl=$name");
        if (type == ObjectType.UserProfile)
        {
            operation.Run("DELETE FROM sys_authorities WHERE holder=$name AND is_authl=0");
            operation.Run("DELETE FROM sys_authl_members WHERE holder=$name");
            operation.Run("DELETE FROM sys_profiles WHERE name=$name");
        }
        if (type == ObjectType.Library)
        {
            operation.Run("DELETE FROM sys_libraries WHERE name=$name");
            operation.Run("DELETE FROM sys_libl WHERE library=$name");
        }
        operation.Run("DELETE FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type");
    }

    public void RemoveFileMember(QualifiedName file, string member)
    {
        new Ipc.Services.Security.ServiceAuthorization(factory).RequireObject(file.Library, file.Name.Value, ObjectType.File, AuthorityBit.ObjectManagement);
        if (!ObjectName.IsValid(member)) throw new ArgumentException("Invalid member name.", nameof(member));
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var descriptor = new SqliteObjectStore(factory).GetRequired(file.Library, file.Name.Value, ObjectType.File);
        var table = $"{file.Library}.{file.Name}.{member}";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' AND mbr=$member";
        command.Parameters.AddWithValue("$lib", file.Library);
        command.Parameters.AddWithValue("$name", file.Name.Value);
        command.Parameters.AddWithValue("$member", member);
        if (Convert.ToInt64(command.ExecuteScalar()) == 0) return;
        if (descriptor.Attribute == "*LF")
        {
            LogicalPathCatalog.RemoveMember(connection, transaction, file, member);
            command.CommandText = "DELETE FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' AND mbr=$member"; command.ExecuteNonQuery();
            command.CommandText = "UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); command.ExecuteNonQuery();
            transaction.Commit(); return;
        }
        command.CommandText = "SELECT count(*) FROM sys_file_defs d,json_each(d.def,'$.logical.members') lm,json_each(lm.value) bm WHERE json_extract(d.def,'$.logical.sourceLibrary')=$lib AND json_extract(d.def,'$.logical.sourceFile')=$name AND bm.value=$member";
        if (Convert.ToInt64(command.ExecuteScalar()) != 0) throw new CpfException("IPC0201", "Physical member is bound by a logical member; remove its bindings first.");
        if (MemberTableDependencies.HasExternalReferences(connection, transaction, new[] { table }))
            throw new CpfException("IPC0201", "Member has SQL dependents.");
        command.CommandText = $"DROP TABLE {Quote(table)}";
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM sys_file_members WHERE lib=$lib AND name=$name AND type='*FILE' AND mbr=$member";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public void Relocate(QualifiedName source, string type, QualifiedName target, bool copy = false, string? newOwner = null)
    {
        new ObjectDescriptor { Key = source, ObjectType = type }.ValidateIdentity();
        new ObjectDescriptor { Key = target, ObjectType = type }.ValidateIdentity();
        var authorization = new Ipc.Services.Security.ServiceAuthorization(factory);
        if (copy && Ipc.Services.Events.OperationIdentity.Current is { } identity)
        {
            newOwner ??= identity.Principal;
            if (newOwner != identity.Principal) authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.SecurityAdministrator);
        }
        authorization.RequireObject(source.Library, source.Name.Value, type,
            copy ? AuthorityBit.ObjectOperate | AuthorityBit.Read : AuthorityBit.ObjectManagement);
        if (!copy) new Ipc.Services.Work.JobLockStore(factory).RequireNoPersistentAllocation(new(source.Library, source.Name.Value, type));
        new Ipc.Services.Work.JobLockStore(factory).RequireObjectMutation(target.Library, target.Name.Value, type);
        authorization.RequireObject("QSYS", target.Library, ObjectType.Library, AuthorityBit.ObjectOperate | AuthorityBit.Add, checkLibrary: false);
        if (copy && type == ObjectType.Program && newOwner is not null)
        {
            var program = new SqliteObjectStore(factory).GetForAuthorization(source.Library, source.Name.Value, type);
            if (program is not null)
            {
                program.Owner = newOwner;
                authorization.RequireAdoption(program);
            }
        }
        if (newOwner is not null && (!copy || !ObjectName.IsValid(newOwner)))
            throw new ArgumentException("A new owner is supported only for a copy and must be a valid profile name.");
        if (type == ObjectType.UserProfile)
            throw new CpfException("IPC0003", "Profile identities cannot be renamed, moved, or copied through generic object operations.");
        using var connection = factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!copy && type is ObjectType.File or ObjectType.Library)
        {
            using var dependents = connection.CreateCommand(); dependents.Transaction = transaction;
            dependents.CommandText = "SELECT lib,name FROM sys_file_defs WHERE json_extract(def,'$.logical.sourceLibrary')=$library AND ($all OR json_extract(def,'$.logical.sourceFile')=$file) ORDER BY lib,name LIMIT 4001";
            dependents.Parameters.AddWithValue("$library", type == ObjectType.Library ? source.Name.Value : source.Library);
            dependents.Parameters.AddWithValue("$file", source.Name.Value); dependents.Parameters.AddWithValue("$all", type == ObjectType.Library);
            var logicalFiles = new List<(string Library, string Name)>();
            using (var reader = dependents.ExecuteReader()) while (reader.Read()) logicalFiles.Add((reader.GetString(0), reader.GetString(1)));
            if (logicalFiles.Count > 4000) throw new CpfException("IPC0201", "Relocation exceeds 4000 dependent logical files.");
            var locks = new Ipc.Services.Work.JobLockStore(factory);
            foreach (var logicalFile in logicalFiles)
            {
                locks.RequireNoPersistentAllocation(new(logicalFile.Library, logicalFile.Name, ObjectType.File));
                locks.RequireObjectMutation(logicalFile.Library, logicalFile.Name, ObjectType.File);
            }
        }
        if (type == ObjectType.Library) RenameLibrary(connection, transaction, source, target, copy);
        else RelocateOne(connection, transaction, source, type, target, copy, newOwner);
        transaction.Commit();
    }

    private static void RelocateOne(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName source, string type, QualifiedName target, bool copy, string? newOwner = null)
    {
        var op = new Operation(connection, transaction, source, target, type, newOwner);
        if (op.Number("SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type") != 1)
            throw new CpfException("CPF9801", "Source object not found.");
        if (op.Number("SELECT count(*) FROM sys_objects WHERE lib='QSYS' AND name=$targetLib AND type='*LIB'") != 1)
            throw new CpfException("CPF9810", "Target library not found.");
        if (op.Number("SELECT count(*) FROM sys_objects WHERE lib=$targetLib AND name=$targetName AND type=$type") != 0)
            throw new CpfException("IPC0202", "Target object already exists.");

        if (copy && type == ObjectType.Command && op.Number("SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type='*CMD' AND attribute='BUILTIN'") != 0)
            throw new CpfException("IPC0136", "Built-in adapters cannot be duplicated; create a user command with CRTCMD.");
        if (!copy && op.Number("SELECT count(*) FROM sys_object_dependencies WHERE source_type='*CMD' AND target_lib=$lib AND target_name=$name AND target_type=$type") > 0)
            throw new CpfException("IPC0201", "Object is bound by a command definition; remove or recompile its commands before relocation.");
        if (!copy) ValidateWorkOperation(op, type);
        var tables = op.Members().Select(member =>
            (Source: $"{source.Library}.{source.Name}.{member}", Target: $"{target.Library}.{target.Name}.{member}")).ToArray();
        if (copy) new MemberTableCopy(connection, transaction).Copy(tables);
        else foreach (var table in tables) op.Run($"ALTER TABLE {Quote(table.Source)} RENAME TO {Quote(table.Target)}");

        if (copy)
        {
            op.Run("""
                INSERT INTO sys_objects SELECT $targetLib,$targetName,type,coalesce($copyOwner,owner),$now,$now,description,
                    ccsid,attribute,format,public_authority,source,json_remove(attrs,'$."ipc.signature"')
                FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type
                """);
            op.Run("INSERT INTO sys_file_defs SELECT $targetLib,$targetName,type,json_set(def,'$.name',$targetName) FROM sys_file_defs WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("INSERT INTO sys_file_members SELECT $targetLib,$targetName,type,mbr,$now FROM sys_file_members WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("INSERT INTO sys_authorities SELECT $targetLib,$targetName,type,holder,is_authl,bits FROM sys_authorities WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("""
                INSERT INTO sys_object_dependencies
                SELECT $targetLib,$targetName,source_type,
                    CASE WHEN target_lib=$lib AND target_name=$name AND target_type=$type THEN $targetLib ELSE target_lib END,
                    CASE WHEN target_lib=$lib AND target_name=$name AND target_type=$type THEN $targetName ELSE target_name END,target_type
                FROM sys_object_dependencies WHERE source_lib=$lib AND source_name=$name AND source_type=$type
                """);
            if (type == ObjectType.SubsystemDescription)
            {
                op.Run("INSERT INTO sys_subsystems SELECT $targetName,description,'Stopped',max_active,$targetLib FROM sys_subsystems WHERE library=$lib AND name=$name");
                op.Run("INSERT INTO sys_routing SELECT $workTarget,seq,compare_value,program,user_class,compare_mode,start_position,class_lib,class_name FROM sys_routing WHERE subsystem=$workSource");
            }
            if (type == ObjectType.JobQueue)
                op.Run("INSERT INTO sys_jobqs SELECT $targetName,$targetLib,description,held FROM sys_jobqs WHERE library=$lib AND name=$name");
            if (type == ObjectType.Class)
                op.Run("INSERT INTO sys_classes SELECT $targetLib,$targetName,type,run_priority,time_slice_ms FROM sys_classes WHERE library=$lib AND name=$name");
            if (type == ObjectType.JobDescription)
            {
                op.Run("INSERT INTO sys_job_descriptions(library,name,type,queue_lib,queue_name,priority,run_as,routing_data,current_library,library_mode) SELECT $targetLib,$targetName,type,queue_lib,queue_name,priority,run_as,routing_data,current_library,library_mode FROM sys_job_descriptions WHERE library=$lib AND name=$name");
                op.Run("INSERT INTO sys_jobd_libraries(jobd_lib,jobd_name,ordinal,library) SELECT $targetLib,$targetName,ordinal,library FROM sys_jobd_libraries WHERE jobd_lib=$lib AND jobd_name=$name");
            }
            if (type == ObjectType.AuthorizationList)
                op.Run("INSERT INTO sys_authl_members SELECT $targetName,holder,bits FROM sys_authl_members WHERE authl=$name");
            if (type == ObjectType.Menu)
            {
                op.Run("INSERT INTO sys_menus SELECT $targetLib,$targetName,title FROM sys_menus WHERE library=$lib AND name=$name");
                op.Run("INSERT INTO sys_menu_options SELECT $targetLib,$targetName,ordinal,number,text,CASE WHEN kind='SubMenu' AND target=$lib||'/'||$name THEN $targetLib||'/'||$targetName ELSE target END,kind FROM sys_menu_options WHERE library=$lib AND menu=$name");
            }
        }
        else
        {
            RelocateNamedReferences(op, type);
            if (type == ObjectType.File)
            {
                op.Run("UPDATE sys_objects SET changed=$now WHERE type='*FILE' AND (lib,name) IN (SELECT lib,name FROM sys_file_defs WHERE json_extract(def,'$.logical.sourceLibrary')=$lib AND json_extract(def,'$.logical.sourceFile')=$name)");
                op.Run("UPDATE sys_file_defs SET def=json_set(def,'$.logical.sourceLibrary',$targetLib,'$.logical.sourceFile',$targetName) WHERE json_extract(def,'$.logical.sourceLibrary')=$lib AND json_extract(def,'$.logical.sourceFile')=$name");
            }
            op.Run("UPDATE sys_objects SET lib=$targetLib,name=$targetName,changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("UPDATE sys_file_defs SET lib=$targetLib,name=$targetName,def=json_set(def,'$.name',$targetName) WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("UPDATE sys_file_members SET lib=$targetLib,name=$targetName WHERE lib=$lib AND name=$name AND type=$type");
            op.Run("UPDATE sys_authorities SET lib=$targetLib,name=$targetName WHERE lib=$lib AND name=$name AND type=$type");
            if (type == ObjectType.SubsystemDescription)
            {
                op.Run("UPDATE sys_subsystems SET library=$targetLib,name=$targetName WHERE library=$lib AND name=$name");
                op.Run("UPDATE sys_routing SET subsystem=$workTarget WHERE subsystem=$workSource");
            }
            if (type == ObjectType.JobQueue)
                op.Run("UPDATE sys_jobqs SET library=$targetLib,name=$targetName WHERE library=$lib AND name=$name");
            if (type == ObjectType.AuthorizationList)
            {
                op.Run("UPDATE sys_authl_members SET authl=$targetName WHERE authl=$name");
                op.Run("UPDATE sys_authorities SET holder=$targetName WHERE holder=$name AND is_authl=1");
            }
            if (type == ObjectType.Menu)
            {
                op.Run("UPDATE sys_menus SET library=$targetLib,name=$targetName WHERE library=$lib AND name=$name");
                op.Run("UPDATE sys_menu_options SET library=$targetLib,menu=$targetName WHERE library=$lib AND menu=$name");
            }
        }
    }

    private static void RenameLibrary(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName source, QualifiedName target, bool copy)
    {
        if (copy || source.Library != "QSYS" || target.Library != "QSYS")
            throw new CpfException("IPC0003", "Libraries can be renamed in QSYS; use save/restore to duplicate a library.");
        var root = new Operation(connection, transaction, source, target, ObjectType.Library);
        EnsureLibraryAvailable(root, source.Name.Value);
        if (root.Number("SELECT count(*) FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type") != 1)
            throw new CpfException("CPF9810", "Library not found.");
        if (root.Number("SELECT count(*) FROM sys_objects WHERE lib=$targetLib AND name=$targetName AND type=$type") != 0)
            throw new CpfException("IPC0202", "Target library already exists.");
        using var members = connection.CreateCommand();
        members.Transaction = transaction;
        members.CommandText = "SELECT name,type FROM sys_objects WHERE lib=$library ORDER BY name,type";
        members.Parameters.AddWithValue("$library", source.Name.Value);
        var objects = new List<(string Name, string Type)>();
        using (var reader = members.ExecuteReader())
            while (reader.Read()) objects.Add((reader.GetString(0), reader.GetString(1)));
        root.Run("UPDATE sys_objects SET name=$targetName,changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE lib=$lib AND name=$name AND type=$type");
        foreach (var item in objects)
            RelocateOne(connection, transaction, new QualifiedName(source.Name.Value, item.Name), item.Type,
                new QualifiedName(target.Name.Value, item.Name), copy: false);
        root.Run("UPDATE sys_libraries SET name=$targetName WHERE name=$name");
        root.Run("UPDATE sys_libl SET library=$targetName WHERE library=$name");
    }

    private static void ValidateWorkOperation(Operation op, string type)
    {
        if (type == ObjectType.SubsystemDescription && (
            op.Number("SELECT count(*) FROM sys_subsystems WHERE library=$lib AND name=$name AND status<>'Stopped'") != 0 ||
            op.Number("SELECT count(*) FROM sys_jobs WHERE status IN ('Active','MessageWait','JobQueue','Held') AND (subsystem=$workSource OR jobq=$workSource)") != 0))
            throw new CpfException("IPC0201", "Stop the subsystem and finish its jobs before relocating or deleting it.");
        if (type == ObjectType.JobQueue && op.Number("SELECT count(*) FROM sys_jobs WHERE status IN ('Active','MessageWait','JobQueue','Held') AND (jobq=$lib||'/'||$name OR (jobq=$name AND $lib IN ('QGPL','QUSRSYS','QSYS')))") != 0)
            throw new CpfException("IPC0201", "Finish or move queued jobs before relocating or deleting their queue.");
    }

    private static void ValidateProfileDeletion(Operation op, QualifiedName source)
    {
        if (source.Library != "QSYS" || new[] { "QSECOFR", "QSECADM", "QSYS", "QSYSOPR", "QPGMR", "QUSER" }.Contains(source.Name.Value))
            throw new CpfException("CPF2283", "A shipped system profile cannot be deleted.");
        if (op.Number("SELECT count(*) FROM sys_objects WHERE owner=$name AND NOT(lib=$lib AND name=$name AND type='*USRPRF')") != 0)
            throw new CpfException("IPC0201", "Profile owns objects; transfer or delete them before deleting the profile.");
        if (op.Number("SELECT count(*) FROM sys_profiles WHERE name<>$name AND (group_profile=$name OR owner=$name)") != 0)
            throw new CpfException("IPC0201", "Profile is referenced as another profile's group or owner.");
        if (op.Number("SELECT count(*) FROM sys_jobs WHERE status IN ('Active','MessageWait','JobQueue','Held') AND (user=$name OR user_profile=$name)") != 0)
            throw new CpfException("IPC0201", "Profile has active or queued jobs.");
    }

    private static void EnsureLibraryAvailable(Operation op, string name)
    {
        if (ObjectName.IsSystemLibrary(name) || LibraryNames.SystemDefaults.Any(library => library.Name == name))
            throw new CpfException("IPC0201", "A shipped system library cannot be renamed or deleted.");
        if (op.Number("SELECT count(*) FROM sys_jobs WHERE status IN ('Active','MessageWait','JobQueue','Held') AND (current_lib=$name OR instr(' '||replace(libl,',',' ')||' ', ' '||$name||' ')>0)") != 0)
            throw new CpfException("IPC0201", "Library is referenced by an active or queued job.");
        if (op.Number("SELECT count(*) FROM sys_profiles WHERE initial_curlib=$name") != 0)
            throw new CpfException("IPC0201", "Library is configured as a profile's initial current library.");
        if (op.Number("SELECT count(*) FROM sys_sysvals WHERE name IN ('QSYSLIBL','QUSRLIBL') AND instr(' '||replace(value,',',' ')||' ', ' '||$name||' ')>0") != 0)
            throw new CpfException("IPC0201", "Library is configured in a system library-list value.");
    }

    private static bool HasNamedReferences(Operation op, string type) => type switch
    {
        ObjectType.Menu => op.Number("SELECT count(*) FROM sys_menu_options WHERE kind='SubMenu' AND target=$lib||'/'||$name AND NOT(library=$lib AND menu=$name)") != 0 ||
            op.Number("SELECT count(*) FROM sys_profiles WHERE initial_menu=$lib||'/'||$name") != 0,
        ObjectType.Program => op.Number("SELECT count(*) FROM sys_profiles WHERE initial_program=$lib||'/'||$name") != 0 ||
            op.Number("SELECT count(*) FROM sys_routing WHERE program=$lib||'/'||$name") != 0,
        ObjectType.Class => op.Number("SELECT count(*) FROM sys_routing WHERE class_lib=$lib AND class_name=$name") != 0,
        ObjectType.JobQueue => op.Number("SELECT count(*) FROM sys_jobq_entries WHERE queue_lib=$lib AND queue_name=$name") != 0 ||
            op.Number("SELECT count(*) FROM sys_job_descriptions WHERE queue_lib=$lib AND queue_name=$name") != 0,
        _ => false,
    };

    private static void RelocateNamedReferences(Operation op, string type)
    {
        if (type == ObjectType.Class)
            op.Run("UPDATE sys_routing SET class_lib=$targetLib,class_name=$targetName WHERE class_lib=$lib AND class_name=$name");
        if (type == ObjectType.Menu)
        {
            op.Run("UPDATE sys_objects SET changed=$now,attrs=json_remove(attrs,'$.\"ipc.signature\"') WHERE type='*MENU' AND (lib,name) IN (SELECT library,menu FROM sys_menu_options WHERE kind='SubMenu' AND target=$lib||'/'||$name)");
            op.Run("UPDATE sys_menu_options SET target=$targetLib||'/'||$targetName WHERE kind='SubMenu' AND target=$lib||'/'||$name");
            op.Run("UPDATE sys_profiles SET initial_menu=$targetLib||'/'||$targetName WHERE initial_menu=$lib||'/'||$name");
        }
        if (type == ObjectType.Program)
        {
            op.Run("UPDATE sys_profiles SET initial_program=$targetLib||'/'||$targetName WHERE initial_program=$lib||'/'||$name");
            op.Run("UPDATE sys_routing SET program=$targetLib||'/'||$targetName WHERE program=$lib||'/'||$name");
        }
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private sealed class Operation(SqliteConnection connection, SqliteTransaction transaction,
        QualifiedName source, QualifiedName target, string type, string? newOwner = null)
    {
        private SqliteCommand Command(string sql)
        {
            var cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$lib", source.Library);
            cmd.Parameters.AddWithValue("$name", source.Name.Value);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$copyOwner", (object?)newOwner ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$targetLib", target.Library);
            cmd.Parameters.AddWithValue("$targetName", target.Name.Value);
            cmd.Parameters.AddWithValue("$workSource", source.Library == "QSYS" ? source.Name.Value : source.ToString());
            cmd.Parameters.AddWithValue("$workTarget", target.Library == "QSYS" ? target.Name.Value : target.ToString());
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            return cmd;
        }
        public void Run(string sql) { using var cmd = Command(sql); cmd.ExecuteNonQuery(); }
        public long Number(string sql) { using var cmd = Command(sql); return Convert.ToInt64(cmd.ExecuteScalar()); }
        public IReadOnlyList<string> Members()
        {
            using var cmd = Command("SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$name AND type=$type AND NOT EXISTS(SELECT 1 FROM sys_objects WHERE lib=$lib AND name=$name AND type=$type AND attribute='*LF') ORDER BY mbr");
            using var reader = cmd.ExecuteReader();
            var list = new List<string>();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }
}
