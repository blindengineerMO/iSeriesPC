using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Definitions;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    public void AddLogicalMember(string library, string name, string member, IReadOnlyList<string>? physicalMembers = null)
    {
        library = library.ToUpperInvariant(); name = name.ToUpperInvariant(); member = member.ToUpperInvariant();
        if (!ObjectName.IsValid(member)) throw LogicalError("Invalid logical member name.");
        Authorize(library, name, AuthorityBit.ObjectManagement);
        using var connection = _factory.Open(); using var transaction = connection.BeginTransaction(deferred: false);
        var definition = GetDefinition(library, name) ?? throw LogicalError("File not found.");
        var logical = definition.Logical ?? throw LogicalError("ADDLFM requires a logical file."); ValidateLogical(logical);
        if (logical.Members.ContainsKey(member)) throw new CpfException("CPF5812", "Logical member already exists.");
        if (logical.Members.Count >= logical.MaximumMembers) throw LogicalError("Logical file has reached its maximum member count.");
        Authorize(logical.SourceLibrary, logical.SourceFile, AuthorityBit.Read | AuthorityBit.ObjectReference);
        var available = ListMembers(logical.SourceLibrary, logical.SourceFile);
        var members = physicalMembers?.ToArray() ?? available.ToArray();
        if (members.Length > 256 || members.Distinct().Count() != members.Length || members.Any(m => !ObjectName.IsValid(m) || !available.Contains(m))) throw LogicalError("Invalid physical member binding.");
        if (logical.Unique && members.Length > 1) throw LogicalError("Unique logical members currently require at most one physical member.");
        var bindings = new Dictionary<string, IReadOnlyList<string>>(logical.Members) { [member] = members };
        definition.Logical = logical with { Members = bindings };
        var physical = GetDefinition(logical.SourceLibrary, logical.SourceFile) ?? throw LogicalError("Missing physical definition.");
        // All authority checks are complete before index creation, the first mutation.
        var uniquePaths = CreateLogicalAccessPaths(connection, transaction, logical, definition.PrimaryFormat, physical.PrimaryFormat, members);
        definition.Logical = definition.Logical with { UniquePaths = uniquePaths };
        using var update = connection.CreateCommand(); update.Transaction = transaction;
        update.CommandText = """
            UPDATE sys_file_defs SET def=$def WHERE lib=$lib AND name=$name AND type='*FILE';
            INSERT INTO sys_file_members VALUES($lib,$name,'*FILE',$member,$now);
            UPDATE sys_objects SET changed=$now WHERE lib=$lib AND name=$name AND type='*FILE';
            """;
        update.Parameters.AddWithValue("$lib", library); update.Parameters.AddWithValue("$name", name);
        update.Parameters.AddWithValue("$member", member); update.Parameters.AddWithValue("$def", definition.ToJson());
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O")); update.ExecuteNonQuery();
        transaction.Commit();
    }
}
