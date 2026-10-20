using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Sql;
using Ipc.Services.Events;

namespace Ipc.Core.Tests;

public sealed partial class SqlTableTests
{
    private void CreationAuthority(string authority)
    {
        var library = _system.Objects.GetRequired("QSYS", "QGPL", ObjectType.Library);
        library.ExtendedAttributes = new Dictionary<string, string>(library.ExtendedAttributes ?? new Dictionary<string, string>()) {
            ["ipc.library.createAuthority"] = authority };
        _system.Objects.Update(library);
    }

    [Theory]
    [InlineData(SqlNaming.Sql, "*ALL", AuthorityBit.None)]
    [InlineData(SqlNaming.Sql, "*EXCLUDE", AuthorityBit.None)]
    [InlineData(SqlNaming.System, "*USE", AuthorityBit.ObjectOperate | AuthorityBit.Read)]
    [InlineData(SqlNaming.System, "*CHANGE", AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete)]
    [InlineData(SqlNaming.System, "*ALL", AuthorityBit.ObjectOperate | AuthorityBit.Read | AuthorityBit.Add | AuthorityBit.Update | AuthorityBit.Delete | AuthorityBit.ObjectManagement | AuthorityBit.ObjectExist | AuthorityBit.ObjectAlter | AuthorityBit.ObjectReference)]
    [InlineData(SqlNaming.System, "*EXCLUDE", AuthorityBit.None)]
    public void Table_public_authority_follows_SQL_naming_or_system_library_CRTAUT(SqlNaming naming, string authority, AuthorityBit expected)
    {
        CreationAuthority(authority);
        var name = naming == SqlNaming.Sql ? "QGPL.DATA" : "QGPL/DATA";
        _files.ExecuteSql("CREATE TABLE " + name + "(ID INTEGER)", Options() with { Naming = naming });
        Assert.Equal(expected, _system.Objects.GetRequired("QGPL", "DATA", ObjectType.File).PublicAuthority);
    }

    [Fact]
    public void A_schema_profile_owns_SQL_tables_and_requires_ADD_to_that_profile_from_another_creator()
    {
        _system.Security.Profiles.Create(new() { Name = "SCHEMAUSR" }); _system.Security.Profiles.Create(new() { Name = "SQLMAKER" });
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("SQLMAKER"), "SqlSchemaCreatorFixture44");
        _system.Libraries.CreateLibrary("SCHEMAUSR");
        _system.Security.Authority.Grant("QSYS", "SCHEMAUSR", ObjectType.Library, "SQLMAKER", Authorities.UseBits | AuthorityBit.Add);
        using (OperationIdentity.Enter("SQLMAKER"))
        {
            Assert.Equal("SQL0551", Assert.Throws<CpfException>(() => Run("CREATE TABLE SCHEMAUSR.DATA(ID INTEGER)")).MessageId);
            _files.ExecuteSql("CREATE TABLE SCHEMAUSR/SYSTABLE(ID INTEGER)", Options() with { Naming = SqlNaming.System });
        }
        Assert.Equal("SQLMAKER", _system.Objects.GetRequired("SCHEMAUSR", "SYSTABLE", ObjectType.File).Owner);
        Assert.False(_files.FileExists("SCHEMAUSR", "DATA"));
        _system.Security.Authority.Grant("QSYS", "SCHEMAUSR", ObjectType.UserProfile, "SQLMAKER", AuthorityBit.Add);
        using (OperationIdentity.Enter("SQLMAKER")) Run("CREATE TABLE SCHEMAUSR.DATA(ID INTEGER)");
        var descriptor = _system.Objects.GetRequired("SCHEMAUSR", "DATA", ObjectType.File);
        Assert.Equal("SCHEMAUSR", descriptor.Owner); Assert.Equal(AuthorityBit.None, descriptor.PublicAuthority);
    }

    [Fact]
    public void SQL_naming_does_not_grant_an_unrelated_user_the_system_naming_tables_public_read_access()
    {
        CreationAuthority("*USE");
        Run("CREATE TABLE QGPL.PRIVDATA(ID INTEGER)");
        _files.ExecuteSql("CREATE TABLE QGPL/PUBDATA(ID INTEGER)", Options() with { Naming = SqlNaming.System });
        Run("INSERT INTO QGPL.PRIVDATA VALUES(1)"); Run("INSERT INTO QGPL.PUBDATA VALUES(2)");
        _system.Security.Profiles.Create(new() { Name = "SQLREADER" });
        using (OperationIdentity.Enter("SQLREADER"))
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => _files.ReadAll("QGPL", "PRIVDATA", "PRIVDATA")).MessageId);
            Assert.Equal(2L, Assert.Single(_files.ReadAll("QGPL", "PUBDATA", "PUBDATA"))["ID"]);
        }
    }

    [Fact]
    public void System_naming_attaches_the_library_authorization_list_atomically_and_SQL_naming_excludes_PUBLIC()
    {
        _system.Objects.Create(new() { Key = new("QSYS", "SQLAUTL"), ObjectType = ObjectType.AuthorizationList, Owner = "QSECOFR" });
        CreationAuthority("SQLAUTL");
        _files.ExecuteSql("CREATE TABLE QGPL/SYSTABLE(ID INTEGER)", Options() with { Naming = SqlNaming.System });
        var system = _system.Objects.GetRequired("QGPL", "SYSTABLE", ObjectType.File);
        Assert.True(system.UseAuthorizationListPublicAuthority); Assert.Equal(AuthorityBit.None, system.PublicAuthority);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM sys_authorities WHERE lib='QGPL' AND name='SYSTABLE' AND type='*FILE' AND holder='SQLAUTL' AND is_authl=1"));
        Run("CREATE TABLE QGPL.SQLTABLE(ID INTEGER)");
        Assert.False(_system.Objects.GetRequired("QGPL", "SQLTABLE", ObjectType.File).UseAuthorizationListPublicAuthority);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM sys_authorities WHERE lib='QGPL' AND name='SQLTABLE' AND is_authl=1"));
    }
}
