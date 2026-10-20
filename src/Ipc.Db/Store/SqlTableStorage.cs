using System.Security.Cryptography;
using System.Text;
using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Ipc.Services.Sqlite;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;
using Ipc.Services.Security;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private void CreateSqlTableDescriptor(SqliteConnection connection, SqliteTransaction transaction, ObjectDescriptor descriptor, SqlNaming naming, bool validateOnly = false, ObjectDescriptor? replacing = null)
    {
        var authorization = new ServiceAuthorization(_factory);
        authorization.RequireObject("QSYS", descriptor.Library, ObjectType.Library, AuthorityBit.ObjectOperate | AuthorityBit.Add, checkLibrary: false);
        var library = ObjectSigningService.Read(connection, transaction, "QSYS", descriptor.Library, ObjectType.Library)
            ?? throw new CpfException("CPF9810", "SQL schema not found.");
        descriptor.Owner = OperationIdentity.Current?.Principal ?? _owner;
        string? authorityList = null;
        if (naming == SqlNaming.Sql)
        {
            var schema = JobTemporaryLibrary.LogicalLibrary(connection, transaction, descriptor.Library);
            if (SqlCatalogContains(connection, transaction, schema, ObjectType.UserProfile))
            {
                if (schema != descriptor.Owner) authorization.RequireObject("QSYS", schema, ObjectType.UserProfile, AuthorityBit.Add);
                descriptor.Owner = schema;
            }
            descriptor.PublicAuthority = AuthorityBit.None;
        }
        else if (replacing is null)
        {
            var authority = library.ExtendedAttributes?.GetValueOrDefault("ipc.library.createAuthority") ?? "*CHANGE";
            if (authority == "*SYSVAL")
            {
                using var value = connection.CreateCommand(); value.Transaction = transaction;
                value.CommandText = "SELECT value FROM sys_sysvals WHERE name='QCRTAUT'";
                authority = value.ExecuteScalar() as string ?? "*CHANGE";
            }
            if (ObjectName.IsValid(authority))
            {
                if (!SqlCatalogContains(connection, transaction, authority, ObjectType.AuthorizationList))
                    throw new CpfException("SQL0204", "Schema creation authorization list not found.");
                authorization.RequireObject("QSYS", authority, ObjectType.AuthorizationList, AuthorityBit.ObjectReference);
                authorityList = authority; descriptor.PublicAuthority = AuthorityBit.None; descriptor.UseAuthorizationListPublicAuthority = true;
            }
            else
            {
                try { descriptor.PublicAuthority = Authorities.FromLevel(authority); }
                catch (ArgumentException) { throw new CpfException("SQL0104", "Unsupported schema creation authority."); }
            }
        }
        if (replacing is not null)
        {
            descriptor.PublicAuthority = replacing.PublicAuthority;
            descriptor.UseAuthorizationListPublicAuthority = replacing.UseAuthorizationListPublicAuthority;
        }
        descriptor.ValidateIdentity();
        new JobLockStore(_factory).RequireAccess(descriptor.Library, descriptor.Name, ObjectType.File, AuthorityBit.ObjectExist);
        if (validateOnly) return;
        // The SQL-specific owner rule above permits *ADD to a matching schema
        // profile. Generic object creation keeps its existing owner authority.
        _objects.CreateAuthorized(descriptor, connection, transaction);
        if (authorityList is not null)
        {
            using var attach = connection.CreateCommand(); attach.Transaction = transaction;
            attach.CommandText = "INSERT INTO sys_authorities(lib,name,type,holder,is_authl,bits) VALUES($lib,$name,'*FILE',$list,1,$bits)";
            attach.Parameters.AddWithValue("$lib", descriptor.Library); attach.Parameters.AddWithValue("$name", descriptor.Name);
            attach.Parameters.AddWithValue("$list", authorityList); attach.Parameters.AddWithValue("$bits", (int)Authorities.AllBits); attach.ExecuteNonQuery();
        }
    }
    private static bool SqlCatalogContains(SqliteConnection connection, SqliteTransaction transaction, string name, string type)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sys_objects WHERE lib='QSYS' AND name=$name AND type=$type";
        command.Parameters.AddWithValue("$name", name); command.Parameters.AddWithValue("$type", type);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }
    private static void ValidateSqlConstraintNames(SqliteConnection connection, SqliteTransaction transaction, string library, FileDefinition definition)
    {
        if (definition.SqlTable is null) return;
        var names = SqlConstraintCatalog.Names(connection, transaction, library);
        foreach (var constraint in definition.SqlTable.Constraints.Where(constraint => constraint.Name is not null))
        {
            if (!names.Add(constraint.Name!)) throw new CpfException("SQL0601", "Constraint " + constraint.Name + " already exists in schema " + library + ".");
        }
        foreach (var foreign in definition.SqlTable.ForeignKeys ?? [])
            if (foreign.Name is not null && !names.Add(foreign.Name)) throw new CpfException("SQL0601", "Constraint name already exists in schema.");
        if (definition.SqlTable.Checks is { } checks)
        {
            var bound = new List<SqlCheckConstraint>();
            foreach (var check in checks)
            {
                _ = SqlCheckRuntime.Validate(definition.PrimaryFormat, check);
                var name = check.Name;
                if (name is not null && !names.Add(name)) throw new CpfException("SQL0601", "Constraint name already exists in schema.");
                if (name is null) do { name = "C" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant(); } while (!names.Add(name));
                bound.Add(check with { Name = name });
            }
            definition.SqlTable = definition.SqlTable with { Checks = bound.Count == 0 ? null : bound };
        }
    }
    private static string SqlColumnDefault(FieldSpec field)
    {
        if (field.CurrentDatetimeDefault) return field.Type switch {
            FieldType.Date => " DEFAULT CURRENT_DATE", FieldType.Time => " DEFAULT CURRENT_TIME",
            FieldType.Timestamp => " DEFAULT (strftime('%Y-%m-%d %H:%M:%f','now') || '000')", _ => throw LogicalError("Invalid SQL default.") };
        return field.DefaultValue is null ? "" : " DEFAULT " + LogicalLiteral(field.DefaultValue.Value is null ? null : SqlFieldValue(field, field.DefaultValue.Value));
    }

    private static void CreateSqlConstraints(SqliteConnection connection, SqliteTransaction transaction, string table, FileDefinition definition)
    {
        if (definition.SqlTable is not { } sql) return;
        foreach (var constraint in sql.Constraints)
        {
            var columns = constraint.Columns.Select(name => definition.PrimaryFormat.Find(name) ?? throw LogicalError("Missing SQL constraint column."));
            var keys = string.Join(',', columns.Select(field => DatabaseSortKeys.KeyColumns(LogicalColumn(field), false, nullable: field.NullCapable)));
            var index = "ipc_sql_unique_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(table + "\0" + keys)));
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "CREATE UNIQUE INDEX " + Quote(index) + " ON " + Quote(table) + "(" + keys + ")";
            command.ExecuteNonQuery();
            command.CommandText = "CREATE UNIQUE INDEX " + Quote(SqlForeignSchema.IndexName(table, constraint.Columns)) + " ON " + Quote(table) + "(" + string.Join(',', constraint.Columns.Select(Quote)) + ")";
            command.ExecuteNonQuery();
        }
        foreach (var foreign in sql.ForeignKeys ?? [])
        {
            using var access = connection.CreateCommand(); access.Transaction = transaction;
            access.CommandText = "CREATE INDEX IF NOT EXISTS " + Quote(SqlForeignSchema.ForeignIndexName(table,foreign.Columns)) + " ON " + Quote(table) + "(" + string.Join(',',foreign.Columns.Select(Quote)) + ")";
            access.ExecuteNonQuery();
            if (SqlForeignSchema.PartialNull(definition.PrimaryFormat, foreign))
            {
                using var hook = connection.CreateCommand(); hook.Transaction = transaction;
                hook.CommandText = SqlForeignSchema.NullTrigger(table, definition.PrimaryFormat, foreign); hook.ExecuteNonQuery();
            }
        }
    }
}
