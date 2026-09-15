using Ipc.Services.Sqlite;

namespace Ipc.Services.Work;

public sealed record RoutingEntry(int Sequence, string CompareValue, string Program, string CompareMode, int StartPosition, string JobClass);

public sealed class RoutingTable
{
    private readonly SqliteConnectionFactory _factory;

    public RoutingTable(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public void EnsureEntry(string subsystem, int sequence, string compareValue, string program, string userClass = "*USER",
        string compareMode = "*EQ", int startPosition = 1, string jobClass = "QSYS/QBATCH", bool? replace = null)
    {
        if (compareValue == "*ANY") compareMode = "*ANY";
        if (sequence is < 1 or > 9999 || compareValue.Length > 80 || startPosition is < 1 or > 80 ||
            compareMode is not ("*ANY" or "*EQ" or "*SECTION") || userClass != "*USER")
            throw new Ipc.Core.Messages.CpfException("IPC0120", "Invalid routing sequence, comparison or position.");
        var key = Ipc.Core.Objects.QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        var classKey = Ipc.Core.Objects.QualifiedName.Parse(jobClass.ToUpperInvariant(), "QSYS");
        var programKey = Ipc.Core.Objects.QualifiedName.Parse(program.ToUpperInvariant(), "QSYS");
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        authorization.RequireObject(key.Library, key.Name.Value, Ipc.Core.Objects.ObjectType.SubsystemDescription, Ipc.Core.Objects.AuthorityBit.ObjectManagement);
        authorization.RequireObject(classKey.Library, classKey.Name.Value, Ipc.Core.Objects.ObjectType.Class, Ipc.Core.Objects.Authorities.UseBits);
        if (programKey.ToString() != "QSYS/QCMD") authorization.RequireObject(programKey.Library, programKey.Name.Value,
            Ipc.Core.Objects.ObjectType.Program, Ipc.Core.Objects.Authorities.UseBits);
        using var connection = _factory.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.Parameters.AddWithValue("$subsystem", WorkName(key));
        cmd.Parameters.AddWithValue("$seq", sequence);
        cmd.CommandText = "SELECT count(*) FROM sys_routing WHERE subsystem=$subsystem AND seq=$seq";
        var exists = Convert.ToInt64(cmd.ExecuteScalar()) != 0;
        if (replace == true && !exists || replace == false && exists)
            throw new Ipc.Core.Messages.CpfException("IPC0120", exists ? "Routing entry already exists." : "Routing entry does not exist.");
        cmd.CommandText = """
            INSERT INTO sys_routing (subsystem, seq, compare_value, program, user_class,compare_mode,start_position,class_lib,class_name)
            VALUES ($subsystem, $seq, $compare, $program, $class,$mode,$position,$classlib,$classname)
            ON CONFLICT(subsystem, seq) DO UPDATE SET
                compare_value = excluded.compare_value,
                program = excluded.program,
                user_class = excluded.user_class,compare_mode=excluded.compare_mode,start_position=excluded.start_position,
                class_lib=excluded.class_lib,class_name=excluded.class_name;
            """;
        cmd.Parameters.AddWithValue("$compare", compareValue);
        cmd.Parameters.AddWithValue("$program", programKey.ToString());
        cmd.Parameters.AddWithValue("$class", userClass);
        cmd.Parameters.AddWithValue("$mode", compareMode); cmd.Parameters.AddWithValue("$position", startPosition);
        cmd.Parameters.AddWithValue("$classlib", classKey.Library); cmd.Parameters.AddWithValue("$classname", classKey.Name.Value);
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<RoutingEntry> Entries(string subsystem)
    {
        var key = Ipc.Core.Objects.QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(key.Library, key.Name.Value,
            Ipc.Core.Objects.ObjectType.SubsystemDescription, Ipc.Core.Objects.Authorities.UseBits);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT seq,compare_value,program,compare_mode,start_position,coalesce(class_lib,'QSYS')||'/'||coalesce(class_name,'QBATCH') FROM sys_routing WHERE subsystem=$subsystem ORDER BY seq";
        command.Parameters.AddWithValue("$subsystem", WorkName(key));
        using var reader = command.ExecuteReader(); var rows = new List<RoutingEntry>();
        while (reader.Read()) rows.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetString(5)));
        return rows;
    }

    public string? Route(string subsystem, string? compareData, string? userProfile = null)
    {
        var key = Ipc.Core.Objects.QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        new Ipc.Services.Security.ServiceAuthorization(_factory).RequireObject(key.Library, key.Name.Value,
            Ipc.Core.Objects.ObjectType.SubsystemDescription, Ipc.Core.Objects.Authorities.UseBits);
        using var connection = _factory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT compare_value, program, compare_mode,start_position FROM sys_routing WHERE subsystem = $subsystem ORDER BY seq";
        cmd.Parameters.AddWithValue("$subsystem", WorkName(key));
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            var compare = reader.GetString(0);
            var program = reader.GetString(1);
            var mode = reader.GetString(2);
            var position = reader.GetInt32(3) - 1;
            var data = compareData ?? "QCMDB";
            if (mode == "*ANY" || mode == "*EQ" && compare == data ||
                mode == "*SECTION" && position <= data.Length && data.AsSpan(position).StartsWith(compare.AsSpan(), StringComparison.Ordinal))
            {
                return program;
            }

        }

        return null;
    }

    internal static string WorkName(Ipc.Core.Objects.QualifiedName key) => key.Library == "QSYS" ? key.Name.Value : key.ToString();

    public void RemoveEntry(string subsystem, int sequence)
    {
        var key = Ipc.Core.Objects.QualifiedName.Parse(subsystem.ToUpperInvariant(), "QSYS");
        var authorization = new Ipc.Services.Security.ServiceAuthorization(_factory);
        authorization.RequireSpecial(Ipc.Core.Security.SpecialAuthority.JobControl);
        authorization.RequireObject(key.Library, key.Name.Value, Ipc.Core.Objects.ObjectType.SubsystemDescription, Ipc.Core.Objects.AuthorityBit.ObjectManagement);
        using var connection = _factory.Open(); using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sys_routing WHERE subsystem=$subsystem AND seq=$sequence";
        command.Parameters.AddWithValue("$subsystem", WorkName(key)); command.Parameters.AddWithValue("$sequence", sequence);
        if (command.ExecuteNonQuery() != 1) throw new Ipc.Core.Messages.CpfException("CPF9801", "Routing entry not found.");
    }
}
