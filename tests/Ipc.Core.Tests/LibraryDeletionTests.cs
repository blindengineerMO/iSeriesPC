using Ipc.Cl.Definitions;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Commands;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class LibraryDeletionTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly SqliteFileStore _files;
    public LibraryDeletionTests()
    {
        _system.Start(); _files = new(_system.Connections, _system.Objects);
        _system.Libraries.CreateLibrary("DELETEME");
        _files.CreateSourceFile("DELETEME", "SOURCE");
        _files.AddMember("DELETEME", "SOURCE", "SECOND");
        _files.SaveSourceMember("DELETEME", "SOURCE", "SOURCE", "preserve on failure", _files.ReadSourceMember("DELETEME", "SOURCE", "SOURCE").Revision);
        _system.Objects.Create(new ObjectDescriptor { Key = new("DELETEME", "CPP"), ObjectType = ObjectType.Program, Attribute = "CLP", Source = "PGM\nENDPGM" });
        var definition = new CommandDefinitionCompiler().Compile("CMD", "DELETEME/CPP");
        new CommandDefinitionStore(_system.Connections).Create("DELETEME", "COMMAND", "CMD", definition, "QSECOFR");
    }
    [Fact]
    public void Command_deletes_library_contents_members_and_dependencies_together()
    {
        var result = new CommandService(_system).Execute("DLTLIB DELETEME"); Assert.False(result.IsError, result.Message);
        Assert.False(_system.Libraries.LibraryExists("DELETEME"));
        using var connection = _system.Connections.Open(); using var query = connection.CreateCommand();
        query.CommandText = "SELECT count(*) FROM sqlite_master WHERE name LIKE 'DELETEME.%'";
        Assert.Equal(0L, query.ExecuteScalar());
        query.CommandText = "PRAGMA foreign_key_check"; Assert.Null(query.ExecuteScalar());
    }
    [Fact]
    public void External_dependency_rolls_back_earlier_deletions_and_member_tables()
    {
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "EXTERNAL"), ObjectType = ObjectType.Program });
        _system.ObjectOperations.AddDependency(new("QGPL", "EXTERNAL"), ObjectType.Program, new("DELETEME", "CPP"), ObjectType.Program);
        Assert.Throws<CpfException>(() => _system.ObjectOperations.DeleteLibrary("DELETEME"));
        Assert.True(_system.Objects.Exists("DELETEME", "COMMAND", ObjectType.Command));
        Assert.Equal(1, _files.RowCount("DELETEME", "SOURCE", "SOURCE"));
        Assert.Equal(2, _files.ListMembers("DELETEME", "SOURCE").Count);
        Assert.True(_system.Libraries.LibraryExists("DELETEME"));
    }
    [Fact]
    public void Denied_child_authority_keeps_all_other_objects()
    {
        _system.Security.Authority.Grant("QSYS", "DELETEME", ObjectType.Library, "QUSER", Authorities.AllBits);
        foreach (var descriptor in _system.Objects.Find("DELETEME", null, null, null))
            _system.Security.Authority.Grant("DELETEME", descriptor.Name, descriptor.ObjectType, "QUSER", descriptor.Name == "SOURCE" ? AuthorityBit.None : Authorities.AllBits);
        using (OperationIdentity.Enter("QUSER")) Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _system.ObjectOperations.DeleteLibrary("DELETEME")).MessageId);
        Assert.True(_system.Objects.Exists("DELETEME", "COMMAND", ObjectType.Command));
        Assert.Equal(1, _files.RowCount("DELETEME", "SOURCE", "SOURCE"));
    }
    [Fact]
    public void System_libraries_and_persistent_allocations_prevent_deletion()
    {
        Assert.Throws<CpfException>(() => _system.ObjectOperations.DeleteLibrary("QGPL"));
        var job = _system.Jobs.CreateInteractive("QUSER");
        using var allocation = new JobLockStore(_system.Connections).Acquire(job.Key, new("DELETEME", "CPP", ObjectType.Program), JobLockMode.SharedRead, TimeSpan.Zero);
        Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => _system.ObjectOperations.DeleteLibrary("DELETEME")).MessageId);
        Assert.True(_system.Objects.Exists("DELETEME", "COMMAND", ObjectType.Command));
    }
    public void Dispose() => _system.Dispose();
}
