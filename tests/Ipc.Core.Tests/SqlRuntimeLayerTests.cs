using System.Reflection;
using Ipc.Db.Definitions;
using Ipc.Db.Sql;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class SqlRuntimeLayerTests
{
    [Fact]
    public void Published_database_and_SQL_type_names_resolve_through_their_original_assemblies()
    {
        var shared = typeof(FileDefinition).Assembly;
        Assert.Equal("Ipc.Sql", shared.GetName().Name);
        foreach (var type in shared.GetExportedTypes())
        {
            var original = type.Namespace == "Ipc.Services.Sqlite" ? "Ipc.Services" : "Ipc.Db";
            Assert.Same(type, Type.GetType(type.FullName + ", " + original, throwOnError: true));
            Assert.Same(type, Assembly.Load(original).GetType(type.FullName!, throwOnError: true));
        }
        Assert.Same(typeof(DatabaseSortKeys), Type.GetType("Ipc.Services.Sqlite.DatabaseSortKeys, Ipc.Services", true));
        Assert.Same(typeof(SqlStatementParser), Type.GetType("Ipc.Db.Sql.SqlStatementParser, Ipc.Db", true));
        Assert.NotNull(typeof(SqlTableDefinition).GetConstructor([typeof(IReadOnlyList<SqlUniqueConstraint>)]));
        Assert.NotNull(typeof(SqlDropKey).GetConstructor([typeof(string), typeof(bool), typeof(bool), typeof(bool)]));
    }
}
