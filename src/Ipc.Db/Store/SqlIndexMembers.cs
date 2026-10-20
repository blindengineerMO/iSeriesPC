using Ipc.Core.Messages;
using Ipc.Db.Definitions;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private List<(string Library, string Name, FileDefinition Definition)> SqlIndexesForMember(SqliteConnection connection, SqliteTransaction transaction, string library, string file)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT lib,name,def FROM sys_file_defs WHERE coalesce(json_extract(def,'$.sqlIndex'),0)=1 AND json_extract(def,'$.logical.sourceLibrary')=$lib AND json_extract(def,'$.logical.sourceFile')=$name";
        command.Parameters.AddWithValue("$lib", library); command.Parameters.AddWithValue("$name", file);
        var result = new List<(string, string, FileDefinition)>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add((reader.GetString(0), reader.GetString(1), FileDefinition.FromJson(reader.GetString(2)))); return result;
    }
    private void ValidateSqlIndexMemberAddition(SqliteConnection connection, SqliteTransaction transaction, string library, string file)
    {
        foreach (var (_, _, definition) in SqlIndexesForMember(connection, transaction, library, file))
        {
            var logical = definition.Logical ?? throw LogicalError("SQL index lost its logical definition."); ValidateLogical(logical);
            var members = logical.Members.Values.SelectMany(value => value).Distinct().Count();
            if (members >= 256 || logical.Unique && members > 0)
                throw new CpfException("SQL0104", "Adding this member requires the remaining multi-member unique/large index support.");
        }
    }
    private void AddSqlIndexMember(SqliteConnection connection, SqliteTransaction transaction, string library, string file, string member, FileDefinition physical)
    {
        foreach (var (indexLibrary, indexName, definition) in SqlIndexesForMember(connection, transaction, library, file))
        {
            var logical = definition.Logical!;
            var bindings = logical.Members.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Append(member).Distinct().ToArray());
            definition.Logical = logical with { Members = bindings,
                UniquePaths = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat, physical.PrimaryFormat, [member]) };
            using var update = connection.CreateCommand(); update.Transaction = transaction;
            update.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
            update.Parameters.AddWithValue("$def", definition.ToJson()); update.Parameters.AddWithValue("$lib", indexLibrary); update.Parameters.AddWithValue("$name", indexName);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.ExecuteNonQuery();
        }
    }
}
