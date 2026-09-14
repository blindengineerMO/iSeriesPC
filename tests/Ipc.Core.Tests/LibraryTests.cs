using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public class LibraryManagerTests
{
    [Fact]
    public void Seed_system_libraries_creates_distinct_libs()
    {
        var store = new Ipc.Core.Objects.InMemoryObjectStore();
        var mgr = new Ipc.Core.Objects.LibraryManager(store, "T");
        mgr.SeedSystemLibraries();

        Assert.Contains("QSYST", store.ListLibraries());
        Assert.Contains("QGPLT", store.ListLibraries());
        Assert.Equal(
            LibraryNames.SystemDefaults.Count,
            store.ListLibraries().Count);
    }

    [Fact]
    public void Create_library_via_manager()
    {
        var store = new Ipc.Core.Objects.InMemoryObjectStore();
        var mgr = new Ipc.Core.Objects.LibraryManager(store);
        mgr.CreateLibrary("APPLIB", "*PROD", "App lib");

        Assert.True(mgr.LibraryExists("APPLIB"));
        Assert.Equal("*PROD", mgr.LibraryType("APPLIB"));
    }

    [Fact]
    public void Duplicate_library_is_rejected()
    {
        var store = new Ipc.Core.Objects.InMemoryObjectStore();
        var mgr = new Ipc.Core.Objects.LibraryManager(store);
        mgr.CreateLibrary("LIB1");

        var ex = Assert.Throws<CpfException>(() => mgr.CreateLibrary("LIB1"));
        Assert.Equal("CPF2110", ex.MessageId);
    }

    [Fact]
    public void QTemp_is_implicit()
    {
        var store = new Ipc.Core.Objects.InMemoryObjectStore();
        var mgr = new Ipc.Core.Objects.LibraryManager(store);
        Assert.True(mgr.LibraryExists("QTEMP"));
        Assert.Throws<InvalidOperationException>(() => mgr.CreateLibrary("QTEMP"));
    }
}