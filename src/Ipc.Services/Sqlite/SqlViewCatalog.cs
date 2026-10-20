using System.Text.Json.Nodes;
using Ipc.Core.Objects;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Sqlite;

internal static class SqlViewCatalog
{
    internal static void RelocateSources(SqliteConnection connection, SqliteTransaction transaction, QualifiedName source, QualifiedName target)
    {
        using var read = connection.CreateCommand(); read.Transaction = transaction;
        read.CommandText = "SELECT lib,name,def FROM sys_file_defs WHERE EXISTS(SELECT 1 FROM json_each(def,'$.sqlView.sources') s WHERE json_extract(s.value,'$.library')=$lib AND json_extract(s.value,'$.name')=$name)";
        read.Parameters.AddWithValue("$lib", source.Library); read.Parameters.AddWithValue("$name", source.Name.Value);
        var views = new List<(string Library, string Name, JsonObject Definition)>();
        using (var reader = read.ExecuteReader()) while (reader.Read()) views.Add((reader.GetString(0), reader.GetString(1), (JsonObject)JsonNode.Parse(reader.GetString(2))!));
        foreach (var (library, name, definition) in views)
        {
            foreach (var item in (JsonArray)definition["sqlView"]!["sources"]!)
                if (item!["library"]!.GetValue<string>() == source.Library && item["name"]!.GetValue<string>() == source.Name.Value)
                { item["library"] = target.Library; item["name"] = target.Name.Value; }
            using var write = connection.CreateCommand(); write.Transaction = transaction;
            write.CommandText = "UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE'; UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE'";
            write.Parameters.AddWithValue("$def", definition.ToJsonString()); write.Parameters.AddWithValue("$lib", library); write.Parameters.AddWithValue("$name", name);
            write.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); write.ExecuteNonQuery();
        }
    }
}
