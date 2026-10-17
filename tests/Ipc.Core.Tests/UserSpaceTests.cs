using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.System;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed class UserSpaceTests : IDisposable
{
    private readonly IpcSystem _system = IpcSystem.Create(":memory:");
    private readonly QualifiedName _name = new("QGPL", "SPACE");
    private UserSpaceStore Store => new(_system.Connections);
    public UserSpaceTests()
    {
        _system.Start(); _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "UserSpaceFixture25");
    }

    [Fact]
    public void Binary_writes_preserve_every_byte_and_detach_returned_storage()
    {
        Store.Create(_name, 512, 0xFF);
        var bytes = Enumerable.Range(0, 256).Select(n => (byte)n).ToArray();
        Store.Write(_name, 129, bytes);
        var expected = Enumerable.Repeat((byte)255, 512).ToArray(); bytes.CopyTo(expected, 128);
        Assert.Equal(expected, Store.Read(_name, 1, 512));
        var detached = Store.Read(_name, 129, 256); Array.Fill(detached, (byte)0);
        Assert.Equal(bytes, Store.Read(_name, 129, 256));
        Store.Write(_name, 1, [0xFE]); Store.Write(_name, 512, [0]);
        Assert.Equal(0xFE, Store.Read(_name, 1, 1)[0]); Assert.Equal(0, Store.Read(_name, 512, 1)[0]);
    }

    [Theory]
    [InlineData(0, 1, "CPF3C0F")]
    [InlineData(1, 0, "CPF3C12")]
    [InlineData(5, 1, "CPF3C14")]
    [InlineData(4, 2, "CPF3C14")]
    [InlineData(int.MaxValue, int.MaxValue, "CPF3C14")]
    public void Invalid_ranges_do_not_change_data(int start, int length, string id)
    {
        Store.Create(_name, 4, 0xAA);
        Assert.Equal(id, Assert.Throws<CpfException>(() => Store.Read(_name, start, length)).MessageId);
        if (length < 100) Assert.Equal(id, Assert.Throws<CpfException>(() => Store.Write(_name, start, new byte[length])).MessageId);
        Assert.Equal(new byte[] { 0xAA, 0xAA, 0xAA, 0xAA }, Store.Read(_name, 1, 4));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(16773121, true)]
    [InlineData(16776705, false)]
    public void Invalid_sizes_leave_no_object(int size, bool aligned)
    {
        Assert.Throws<CpfException>(() => Store.Create(_name, size, optimumAlignment: aligned));
        Assert.False(_system.Objects.Exists("QGPL", "SPACE", ObjectType.UserSpace));
    }

    [Theory]
    [InlineData(true, 16773120)]
    [InlineData(false, 16776704)]
    public void Exact_native_size_limits_and_last_byte_work(bool aligned, int size)
    {
        Store.Create(_name, size, 0xFE, optimumAlignment: aligned);
        Assert.Equal(size, Store.Inspect(_name).Size);
        Assert.Equal(0xFE, Store.Read(_name, size, 1)[0]); Store.Write(_name, size, [0]);
        Assert.Equal(0, Store.Read(_name, size, 1)[0]);
        Assert.Throws<CpfException>(() => Store.ChangeAttributes(_name, size: size + 1));
    }

    [Fact]
    public void Resize_preserves_prefix_and_uses_new_initial_value_for_extension()
    {
        Store.Create(_name, 4, 0xAA); Store.Write(_name, 2, [0, 255]);
        Store.ChangeAttributes(_name, size: 7, initialValue: 0xFE, automaticallyExtendible: true, transferSize: 32);
        Assert.Equal("AA00FFAAFEFEFE", Convert.ToHexString(Store.Read(_name, 1, 7)));
        var attributes = Store.Inspect(_name); Assert.True(attributes.AutomaticallyExtendible); Assert.Equal(32, attributes.TransferSize);
        Store.ChangeAttributes(_name, size: 2); Store.ChangeAttributes(_name, size: 4);
        Assert.Equal("AA00FEFE", Convert.ToHexString(Store.Read(_name, 1, 4)));
        Assert.Throws<CpfException>(() => Store.ChangeAttributes(_name, size: 8, transferSize: 33));
        Assert.Equal(4, Store.Inspect(_name).Size);
    }

    [Fact]
    public void Copy_move_delete_and_library_rename_keep_payloads_bound()
    {
        Store.Create(_name, 4); Store.Write(_name, 1, [255, 0, 254, 1]);
        var catalog = new ObjectCatalogOperations(_system.Connections); QualifiedName copy = new("QGPL", "COPY");
        catalog.Relocate(_name, ObjectType.UserSpace, copy, copy: true);
        Assert.NotEqual(Store.Inspect(_name).Identity, Store.Inspect(copy).Identity);
        Store.Write(copy, 2, [0xAA]); Assert.Equal("FF00FE01", Convert.ToHexString(Store.Read(_name, 1, 4)));
        catalog.Relocate(copy, ObjectType.UserSpace, new("QGPL", "MOVED"));
        Assert.Equal("FFAAFE01", Convert.ToHexString(Store.Read(new("QGPL", "MOVED"), 1, 4)));
        catalog.Delete(new("QGPL", "MOVED"), ObjectType.UserSpace);
        Assert.Throws<CpfException>(() => Store.Read(new("QGPL", "MOVED"), 1, 1));
        _system.Libraries.CreateLibrary("USLIB");
        catalog.Relocate(_name, ObjectType.UserSpace, new("USLIB", "SPACE"));
        catalog.Relocate(new("QSYS", "USLIB"), ObjectType.Library, new("QSYS", "USLIB2"));
        Assert.Equal("FF00FE01", Convert.ToHexString(Store.Read(new("USLIB2", "SPACE"), 1, 4)));
    }

    [Fact]
    public void Replacement_archives_old_bytes_and_preserves_private_and_public_authority()
    {
        Store.Create(_name, 3, 0xFE, authority: "*EXCLUDE");
        _system.Security.Authority.Grant("QGPL", "SPACE", ObjectType.UserSpace, "QUSER", Authorities.UseBits);
        var old = Store.Inspect(_name);
        Assert.Throws<CpfException>(() => Store.Create(_name, 1));
        Assert.Throws<CpfException>(() => Store.Create(_name, 0, replace: true));
        Store.Create(_name, 2, 0xAA, authority: "INVALID!", replace: true);
        Assert.NotEqual(old.Identity, Store.Inspect(_name).Identity);
        Assert.Equal(AuthorityBit.None, _system.Objects.GetRequired("QGPL", "SPACE", ObjectType.UserSpace).PublicAuthority);
        var archive = Assert.Single(_system.Objects.Find("QRPLOBJ", null, ObjectType.UserSpace, null));
        Assert.Equal("SPACE", archive.Description); Assert.Equal(old.Identity, Store.Inspect(archive.Key).Identity);
        Assert.Equal("FEFEFE", Convert.ToHexString(Store.Read(archive.Key, 1, 3)));
        using (OperationIdentity.Enter("QUSER"))
        {
            Assert.Equal("AAAA", Convert.ToHexString(Store.Read(_name, 1, 2)));
            Assert.Throws<CpfException>(() => Store.Write(_name, 1, [0]));
        }
    }

    [Fact]
    public void User_domain_policy_is_live_and_system_domain_remains_available()
    {
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QGPL");
        Store.Create(_name, 1); Assert.Equal("*USER", Store.Inspect(_name).Domain);
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QUSRSYS");
        Store.Create(new("QGPL", "DEFAULT"), 1); Assert.Equal("*SYSTEM", Store.Inspect(new("QGPL", "DEFAULT")).Domain);
        Assert.Throws<CpfException>(() => Store.Create(new("QGPL", "DENIED"), 1, domain: "*USER"));
        Store.Create(new("QGPL", "SYSTEM"), 1, domain: "*SYSTEM");
        Assert.Throws<CpfException>(() => _system.SetSystemValue(SystemValueNames.AllowUserDomain, "MISSING"));
    }

    [Fact]
    public void Live_authority_and_exclusive_allocations_protect_reads_and_mutations()
    {
        Store.Create(_name, 2, authority: "*EXCLUDE");
        _system.Security.Authority.Grant("QGPL", "SPACE", ObjectType.UserSpace, "QUSER", Authorities.ChangeBits);
        using (OperationIdentity.Enter("QUSER"))
        {
            Store.Write(_name, 1, [255]); Assert.Equal(255, Store.Read(_name, 1, 1)[0]);
            Assert.Throws<CpfException>(() => Store.ChangeAttributes(_name, size: 4));
        }
        _system.Security.Authority.Grant("QGPL", "SPACE", ObjectType.UserSpace, "QUSER", AuthorityBit.None);
        using (OperationIdentity.Enter("QUSER")) Assert.Throws<CpfException>(() => Store.Read(_name, 1, 1));
        var holder = _system.Jobs.CreateInteractive("QSECOFR"); var contender = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections);
        using (locks.Acquire(holder.Key, new("QGPL", "SPACE", ObjectType.UserSpace), JobLockMode.Exclusive, TimeSpan.Zero))
        using (OperationIdentity.Enter("QSECOFR", contender.Key))
        using (locks.EnterCommand(contender.Key, CancellationToken.None))
        {
            Assert.Throws<CpfException>(() => Store.Read(_name, 1, 1));
            Assert.Throws<CpfException>(() => Store.Write(_name, 1, [0]));
            Assert.Throws<CpfException>(() => Store.Create(_name, 2, replace: true));
        }
        Assert.Equal(255, Store.Read(_name, 1, 1)[0]);
    }

    [Theory]
    [InlineData(JobLockMode.SharedRead, true)]
    [InlineData(JobLockMode.SharedUpdate, false)]
    [InlineData(JobLockMode.SharedNoUpdate, false)]
    [InlineData(JobLockMode.ExclusiveRead, false)]
    [InlineData(JobLockMode.Exclusive, false)]
    public void Writes_use_exclusive_read_and_attribute_changes_remain_exclusive(JobLockMode mode, bool writable)
    {
        Store.Create(_name, 2, 0xFE);
        var holder = _system.Jobs.CreateInteractive("QSECOFR"); var writer = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections);
        using (locks.Acquire(holder.Key, new("QGPL", "SPACE", ObjectType.UserSpace), mode, TimeSpan.Zero))
        using (OperationIdentity.Enter("QSECOFR", writer.Key))
        using (locks.EnterCommand(writer.Key))
        {
            if (writable)
            {
                Store.Write(_name, 1, [0xAB]);
                var held = locks.Inspect(new("QGPL", "SPACE", ObjectType.UserSpace)).Where(r => r.Job == writer.Key).ToArray();
                Assert.Contains(held, r => r.Mode == JobLockMode.ExclusiveRead);
                Assert.DoesNotContain(held, r => r.Mode is JobLockMode.Exclusive or JobLockMode.SharedUpdate);
            }
            else Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => Store.Write(_name, 1, [0xAB])).MessageId);
            Assert.Throws<CpfException>(() => Store.ChangeAttributes(_name, size: 4));
        }
        Assert.Equal(writable ? "ABFE" : "FEFE", Convert.ToHexString(Store.Read(_name, 1, 2)));
    }

    [Theory]
    [InlineData("*ALL")]
    [InlineData("QTEMP")]
    [InlineData("*DIR")]
    [InlineData("*DIR,QTEMP,QGPL")]
    public void Qtemp_preserves_explicit_system_domain_under_each_policy(string policy)
    {
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, policy);
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        using (OperationIdentity.Enter("QSECOFR", job.Key))
        {
            Store.Create(new("QTEMP", "SYSTEM"), 1, domain: "*SYSTEM");
            Store.Create(new("QTEMP", "USER"), 1, domain: "*USER");
            Store.Create(new("QTEMP", "DEFAULT"), 1);
            Assert.Equal("*SYSTEM", Store.Inspect(new("QTEMP", "SYSTEM")).Domain);
            Assert.Equal("*USER", Store.Inspect(new("QTEMP", "USER")).Domain);
            Assert.Equal("*USER", Store.Inspect(new("QTEMP", "DEFAULT")).Domain);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Domain_policy_blocks_copy_and_move_without_changing_either_object(bool copy)
    {
        _system.Libraries.CreateLibrary("DEST"); Store.Create(_name, 3, 0xFE);
        var before = Store.Inspect(_name);
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QGPL");
        var catalog = new ObjectCatalogOperations(_system.Connections);
        Assert.Equal("CPF3C49", Assert.Throws<CpfException>(() => catalog.Relocate(_name, ObjectType.UserSpace, new("DEST", "SPACE"), copy)).MessageId);
        Assert.Equal(before, Store.Inspect(_name)); Assert.Equal("FEFEFE", Convert.ToHexString(Store.Read(_name, 1, 3)));
        Assert.False(_system.Objects.Exists("DEST", "SPACE", ObjectType.UserSpace));
        Store.Create(new("QGPL", "SYSTEM"), 2, 0xAB, domain: "*SYSTEM");
        catalog.Relocate(new("QGPL", "SYSTEM"), ObjectType.UserSpace, new("DEST", "SYSTEM"), copy);
        Assert.Equal("*SYSTEM", Store.Inspect(new("DEST", "SYSTEM")).Domain);
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,QGPL,DEST");
        catalog.Relocate(_name, ObjectType.UserSpace, new("DEST", "SPACE"), copy);
        Assert.Equal("FEFEFE", Convert.ToHexString(Store.Read(new("DEST", "SPACE"), 1, 3)));
    }

    [Fact]
    public void Library_rename_rechecks_user_domain_policy_and_rolls_back_earlier_moves()
    {
        _system.Libraries.CreateLibrary("OLDLIB");
        Store.Create(new("OLDLIB", "ASYSTEM"), 2, 0xAB, domain: "*SYSTEM");
        Store.Create(new("OLDLIB", "ZUSER"), 2, 0xFE);
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP,OLDLIB");
        var catalog = new ObjectCatalogOperations(_system.Connections);
        Assert.Throws<CpfException>(() => catalog.Relocate(new("QSYS", "OLDLIB"), ObjectType.Library, new("QSYS", "NEWLIB")));
        Assert.True(_system.Libraries.LibraryExists("OLDLIB")); Assert.False(_system.Libraries.LibraryExists("NEWLIB"));
        Assert.Equal("ABAB", Convert.ToHexString(Store.Read(new("OLDLIB", "ASYSTEM"), 1, 2)));
        Assert.Equal("FEFE", Convert.ToHexString(Store.Read(new("OLDLIB", "ZUSER"), 1, 2)));
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "*ALL");
        catalog.Relocate(new("QSYS", "OLDLIB"), ObjectType.Library, new("QSYS", "NEWLIB"));
        Assert.Equal("FEFE", Convert.ToHexString(Store.Read(new("NEWLIB", "ZUSER"), 1, 2)));
    }

    [Fact]
    public void Renaming_an_existing_user_space_preserves_its_domain_after_policy_tightens()
    {
        Store.Create(_name, 2, 0xFE); var before = Store.Inspect(_name);
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP");
        var catalog = new ObjectCatalogOperations(_system.Connections);
        catalog.Relocate(_name, ObjectType.UserSpace, new("QGPL", "RENAMED"));
        Assert.Equal(before, Store.Inspect(new("QGPL", "RENAMED")));
        Assert.Equal("CPF3C49", Assert.Throws<CpfException>(() => catalog.Relocate(new("QGPL", "RENAMED"), ObjectType.UserSpace, new("QGPL", "COPY"), copy: true)).MessageId);
        Assert.Equal("FEFE", Convert.ToHexString(Store.Read(new("QGPL", "RENAMED"), 1, 2)));
    }

    [Fact]
    public void Mixed_directory_policy_validates_all_entries_and_rejects_temporary_backing_names()
    {
        _system.SetSystemValue(SystemValueNames.AllowUserDomain, "*dir qtemp qgpl");
        Assert.Equal("*DIR,QTEMP,QGPL", _system.SystemValues.Get(SystemValueNames.AllowUserDomain).Value);
        foreach (var invalid in new[] { "", "*ALL,QTEMP", "*DIR,QGPL", "QTEMP,QGPL,QGPL", "QTEMP,MISSING", "QTEMP,*INVALID" })
            Assert.Throws<CpfException>(() => _system.SetSystemValue(SystemValueNames.AllowUserDomain, invalid));
        var job = _system.Jobs.CreateInteractive("QSECOFR");
        string physical;
        using (OperationIdentity.Enter("QSECOFR", job.Key)) physical = new JobTemporaryLibrary(_system.Connections).ResolveLibrary("QTEMP");
        Assert.Throws<CpfException>(() => _system.SetSystemValue(SystemValueNames.AllowUserDomain, "QTEMP," + physical));
        Assert.Equal("*DIR,QTEMP,QGPL", _system.SystemValues.Get(SystemValueNames.AllowUserDomain).Value);
    }

    [Fact]
    public void Missing_or_mismatched_payloads_are_rejected()
    {
        Store.Create(_name, 1); var descriptor = _system.Objects.GetRequired("QGPL", "SPACE", ObjectType.UserSpace);
        descriptor.Source = Guid.NewGuid().ToString("N"); _system.Objects.Update(descriptor);
        Assert.Equal("IPC0142", Assert.Throws<CpfException>(() => Store.Read(_name, 1, 1)).MessageId);
        Assert.Throws<CpfException>(() => Store.Create(_name, 1, replace: true));
        Assert.Empty(_system.Objects.Find("QRPLOBJ", null, ObjectType.UserSpace, null));
        Assert.Throws<CpfException>(() => new ObjectCatalogOperations(_system.Connections).Relocate(_name, ObjectType.UserSpace, new("QGPL", "COPY"), copy: true));
        Assert.False(_system.Objects.Exists("QGPL", "COPY", ObjectType.UserSpace));
    }

    [Fact]
    public async Task Concurrent_disjoint_writes_preserve_all_committed_ranges()
    {
        Store.Create(_name, 128);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(part => Task.Run(() => {
            for (var repeat = 0; repeat < 10; repeat++) Store.Write(_name, part * 16 + 1, Enumerable.Repeat((byte)(part + 1), 16).ToArray(), stop.Token);
        })));
        Assert.Equal(Enumerable.Range(1, 8).SelectMany(n => Enumerable.Repeat((byte)n, 16)), Store.Read(_name, 1, 128));
    }

    [Fact]
    public void Cancellation_before_mutation_preserves_bytes_and_identity()
    {
        Store.Create(_name, 2, 255); var before = Store.Inspect(_name);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => Store.Write(_name, 1, [0], stop.Token));
        Assert.Throws<OperationCanceledException>(() => Store.ChangeAttributes(_name, size: 4, cancellationToken: stop.Token));
        Assert.Throws<OperationCanceledException>(() => Store.Create(_name, 3, replace: true, cancellationToken: stop.Token));
        Assert.Equal(before, Store.Inspect(_name)); Assert.Equal(new byte[] { 255, 255 }, Store.Read(_name, 1, 2));
    }
    public void Dispose() => _system.Dispose();
}
