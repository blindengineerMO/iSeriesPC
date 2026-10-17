using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Microsoft.Data.Sqlite;

namespace Ipc.Services.Work;

public sealed partial class UserSpaceStore
{
    /// <summary>Delete exact or terminal-star names. Eligible generic matches commit even
    /// when other matches are denied, allocated or protected by dependencies.</summary>
    public int Delete(string name, string library, Func<string, IEnumerable<string>> searchLibraries,
        CancellationToken cancellationToken = default)
    {
        name = name.ToUpperInvariant(); library = library.ToUpperInvariant();
        var generic = name.EndsWith('*'); var prefix = generic ? name[..^1] : name;
        if (name.Length > 10 || !ObjectName.IsValid(prefix) ||
            !ObjectName.IsValid(library) && library is not ("*LIBL" or "*CURLIB" or "*USRLIBL" or "*ALL" or "*ALLUSR"))
            throw Invalid("Invalid qualified user-space deletion name.");
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = new JobTemporaryLibrary(factory);
        var broad = library is "*ALL" or "*ALLUSR";
        var multiple = generic || broad || library == "*USRLIBL";
        using var connection = factory.Open(); using var transaction = BeginWrite(connection, cancellationToken);
        var libraries = broad ? DeletionLibraries(connection, transaction, library == "*ALLUSR") :
            library is "*LIBL" or "*CURLIB" or "*USRLIBL" ? searchLibraries(library).Distinct(StringComparer.Ordinal).ToArray() : [library];
        var eligible = new List<QualifiedName>(); var failed = 0; var inaccessibleLibraries = 0;
        foreach (var logicalLibrary in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physical = temporary.ResolveLibrary(logicalLibrary);
            using var query = connection.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT 1 FROM sys_objects WHERE lib='QSYS' AND name=$library AND type='*LIB'";
            query.Parameters.AddWithValue("$library", physical);
            if (query.ExecuteScalar() is null)
            {
                if (!broad) throw new CpfException("CPF2110", "Library not found.");
                continue;
            }
            try
            {
                _authorization.RequireObject("QSYS", physical, ObjectType.Library, AuthorityBit.ObjectOperate, checkLibrary: false, acquireLocks: false);
                _locks.RequireAccess("QSYS", physical, ObjectType.Library, Authorities.UseBits);
            }
            catch (CpfException failure) when (failure.MessageId is "CPF9802" or "CPF1002")
            {
                if (!broad) throw new CpfException(failure.MessageId == "CPF9802" ? "CPF2182" : "CPF2113", "Library cannot be searched for deletion.");
                inaccessibleLibraries++; continue;
            }
            query.CommandText = generic
                ? "SELECT name FROM sys_objects WHERE lib=$library AND type='*USRSPC' AND substr(name,1,length($name))=$name ORDER BY name"
                : "SELECT name FROM sys_objects WHERE lib=$library AND type='*USRSPC' AND name=$name";
            query.Parameters.AddWithValue("$name", prefix);
            var matches = new List<string>(); using (var reader = query.ExecuteReader()) while (reader.Read()) matches.Add(reader.GetString(0));
            foreach (var match in matches)
            {
                cancellationToken.ThrowIfCancellationRequested(); var space = new QualifiedName(physical, match);
                try
                {
                    // Check all authorities before the first catalog mutation. Authorization
                    // uses independent connections, including shared-memory catalogs.
                    _authorization.RequireObject(physical, match, ObjectType.UserSpace, AuthorityBit.ObjectExist, acquireLocks: false);
                    _locks.RequireObjectMutation(physical, match, ObjectType.UserSpace);
                    _locks.RequireNoPersistentAllocation(new(physical, match, ObjectType.UserSpace));
                    eligible.Add(space);
                }
                catch (CpfException failure) when (failure.MessageId is "CPF9802" or "CPF1002")
                {
                    if (!multiple) throw new CpfException(failure.MessageId == "CPF9802" ? "CPF2189" : "CPF2114", "User space cannot be deleted.");
                    failed++;
                }
            }
            if (library == "*LIBL" && matches.Count != 0) break;
        }
        var deleted = 0;
        foreach (var space in eligible)
        {
            cancellationToken.ThrowIfCancellationRequested(); transaction.Save("delete_user_space");
            try
            {
                ObjectCatalogOperations.DeleteCore(space, ObjectType.UserSpace, connection, transaction);
                transaction.Release("delete_user_space"); deleted++;
            }
            catch (CpfException)
            {
                transaction.Rollback("delete_user_space"); transaction.Release("delete_user_space");
                if (!multiple) throw;
                failed++;
            }
        }
        cancellationToken.ThrowIfCancellationRequested(); transaction.Commit();
        // Report partial failure only after successful deletions are durably committed.
        if (failed != 0 || inaccessibleLibraries != 0)
            throw new CpfException(deleted == 0 ? "CPF2125" : "CPF2117",
                $"{deleted} user spaces deleted; {failed} matched objects not deleted; {inaccessibleLibraries} libraries unavailable.");
        if (deleted == 0) throw new CpfException(multiple ? "CPF2125" : "CPF2105", "No matching user spaces found.");
        return deleted;
    }

    private static string[] DeletionLibraries(SqliteConnection connection, SqliteTransaction transaction, bool userOnly)
    {
        using var query = connection.CreateCommand(); query.Transaction = transaction;
        query.CommandText = """
            SELECT name FROM sys_objects WHERE lib='QSYS' AND type='*LIB' AND name<>'QTEMP'
              AND NOT EXISTS(SELECT 1 FROM sys_job_temp_libraries t WHERE t.library=sys_objects.name)
            ORDER BY name
            """;
        var libraries = new List<string>();
        using (var reader = query.ExecuteReader()) while (reader.Read())
        {
            var library = reader.GetString(0);
            if (!userOnly || IsUserLibrary(library)) libraries.Add(library);
        }
        // A job's logical QTEMP is the only temporary library visible to *ALL.
        if (!userOnly && OperationIdentity.Current?.Job is not null) libraries.Add(LibraryNames.QTemp);
        return libraries.ToArray();
    }

    private static bool IsUserLibrary(string library)
    {
        if (library is "#CGULIB" or "#COBLIB" or "#DFULIB" or "#DSULIB" or "#RPGLIB" or "#SDALIB" or "#SEULIB") return false;
        if (!library.StartsWith('Q')) return true;
        if (UserLibraries.Contains(library)) return true;
        return library.StartsWith("QRCL", StringComparison.Ordinal) && library.Length == 9 && library.Skip(4).All(char.IsAsciiDigit) ||
            library.StartsWith("QSYS2", StringComparison.Ordinal) && library.Length == 10 && library.Skip(5).All(char.IsAsciiDigit) ||
            library.Length == 10 && library.StartsWith("QUSRV", StringComparison.Ordinal) && library[6] == 'R' && library[8] == 'M' &&
            char.IsAsciiDigit(library[5]) && char.IsAsciiDigit(library[7]) && char.IsAsciiDigit(library[9]);
    }
    private static readonly HashSet<string> UserLibraries = new(StringComparer.Ordinal)
    {
        "QDSNX", "QGPL", "QGPL38", "QMGTC", "QMGTC2", "QMPGDATA", "QMQMDATA", "QMQMPROC", "QPFRDATA", "QRCL", "QSRVAGT",
        "QSYS2", "QS36F", "QUSER38", "QUSRADSM", "QUSRBRM", "QUSRDIRCF", "QUSRDIRCL", "QUSRDIRDB", "QUSRIJS", "QUSRINFSKR",
        "QUSRNOTES", "QUSROND", "QUSRPOSGS", "QUSRPOSSA", "QUSRPYMSVR", "QUSRRDARS", "QUSRSYS", "QUSRVI", "QWQCENT", "QWQREPOS"
    };
}
