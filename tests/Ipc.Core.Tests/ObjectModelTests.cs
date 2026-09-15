using Ipc.Core.Messages;
using Ipc.Core.Objects;

namespace Ipc.Core.Tests;

public class ObjectNameTests
{
    [Theory]
    [InlineData("CUSTOMER")]
    [InlineData("INVC#1")]
    [InlineData("A$B@1")]
    [InlineData("Q")]
    [InlineData("1234567890")]
    public void Valid_names_are_accepted(string name) =>
        Assert.True(ObjectName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData("TOOLONGOBJECTNAM")]
    [InlineData("with lower")]
    [InlineData("has space")]
    [InlineData("*")]
    [InlineData("A/B")]
    public void Invalid_names_are_rejected(string name) =>
        Assert.False(ObjectName.IsValid(name));

    [Theory]
    [InlineData("QSYS")]
    [InlineData("QUSRSYS")]
    public void Quick_names_are_reserved(string name) =>
        Assert.True(new ObjectName(name).IsReserved);

    [Theory]
    [InlineData("MAIN")]
    [InlineData("CUST")]
    public void Plain_names_are_not_reserved(string name) =>
        Assert.False(new ObjectName(name).IsReserved);

    [Theory]
    [InlineData("QGPL")]
    [InlineData("QTEMP")]
    [InlineData("QSYS")]
    public void System_libraries_are_known(string name) =>
        Assert.True(ObjectName.IsSystemLibrary(name));
}

public class QualifiedNameTests
{
    [Fact]
    public void Parse_splits_library_and_name()
    {
        var q = QualifiedName.Parse("MYLIB/CUSTMAST");
        Assert.Equal("MYLIB", q.Library);
        Assert.Equal("CUSTMAST", q.Name.Value);
    }

    [Fact]
    public void Parse_allows_special_library_markers()
    {
        Assert.Equal("*LIBL", QualifiedName.Parse("*LIBL/PGM001").Library);
        Assert.Equal("*CURLIB", QualifiedName.Parse("*CURLIB/PGM001").Library);
    }

    [Fact]
    public void Parse_rejects_unqualified()
    {
        Assert.Throws<FormatException>(() => QualifiedName.Parse("ONLYNAME"));
    }

    [Fact]
    public void ToString_round_trips()
    {
        var q = QualifiedName.Parse("LIB1/OBJ1");
        Assert.Equal("LIB1/OBJ1", q.ToString());
    }

    [Fact]
    public void LibrariesIn_resolves_libl_specials()
    {
        var q = QualifiedName.Parse("*LIBL/PGM1");
        var libs = QualifiedName.LibrariesIn(q, "CURLIB", new[] { "QSYS", "QGPL" }, new[] { "USRLB1" });
        Assert.Equal(new[] { "QSYS", "QGPL", "CURLIB", "USRLB1" }, libs);
    }
}

public class AuthorityTests
{
    [Theory]
    [InlineData(Authorities.Use, AuthorityBit.ObjectOperate | AuthorityBit.Read)]
    [InlineData(Authorities.Change,
        AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Add |
        AuthorityBit.Update | AuthorityBit.Delete)]
    public void Levels_map_to_bits(string level, AuthorityBit expected) =>
        Assert.Equal(expected, Authorities.FromLevel(level));

    [Fact]
    public void All_includes_object_exist() =>
        Assert.True((Authorities.AllBits & AuthorityBit.ObjectExist) != 0);

    [Fact]
    public void Exclude_is_none() =>
        Assert.Equal(AuthorityBit.None, Authorities.FromLevel(Authorities.Exclude));
}

public class ObjectStoreTests
{
    private static InMemoryObjectStore NewStore()
    {
        var store = new InMemoryObjectStore();
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("MYLIB", "CUSTMAST"),
            ObjectType = ObjectType.File,
            Attribute = "*PF",
        });
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("MYLIB", "CUSTPRG"),
            ObjectType = ObjectType.Program,
        });
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("MYLIB", "INVC#1"),
            ObjectType = ObjectType.File,
            Attribute = "*PF",
        });
        return store;
    }

    [Fact]
    public void Create_and_get()
    {
        var store = NewStore();
        var d = store.Get("MYLIB", "CUSTMAST", ObjectType.File);
        Assert.NotNull(d);
        Assert.Equal("*PF", d!.Attribute);
    }

    [Fact]
    public void Get_required_throws_cpf9801()
    {
        var store = NewStore();
        var ex = Assert.Throws<CpfException>(() =>
            store.GetRequired("NOLIB", "NOOBJ", ObjectType.File));
        Assert.Equal("CPF9801", ex.MessageId);
    }

    [Fact]
    public void Delete_removes_object()
    {
        var store = NewStore();
        store.Delete("MYLIB", "INVC#1", ObjectType.File);
        Assert.Null(store.Get("MYLIB", "INVC#1", ObjectType.File));
    }

    [Fact]
    public void Find_with_generic_pattern()
    {
        var store = NewStore();
        var files = store.Find("MYLIB", "CUST*", null, null);
        Assert.Equal(new[] { "CUSTMAST", "CUSTPRG" }, files.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void Find_filters_by_type()
    {
        var store = NewStore();
        var files = store.Find("MYLIB", null, ObjectType.File, null);
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void List_libraries_returns_objects_of_type_lib()
    {
        var store = NewStore();
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("QSYS", "MYLIB"),
            ObjectType = ObjectType.Library,
        });
        store.Create(new ObjectDescriptor
        {
            Key = new QualifiedName("QSYS", "LIB2"),
            ObjectType = ObjectType.Library,
        });
        Assert.Equal(new[] { "LIB2", "MYLIB" }, store.ListLibraries());
    }

    [Fact]
    public void Update_touches_changed_time()
    {
        var store = NewStore();
        var d = store.GetRequired("MYLIB", "CUSTMAST", ObjectType.File);
        var before = d.Changed;
        Thread.Sleep(5);
        d.Description = "updated";
        store.Update(d);
        Assert.True(d.Changed >= before);
        Assert.Equal("updated", store.Get("MYLIB", "CUSTMAST", ObjectType.File)!.Description);
    }
}
