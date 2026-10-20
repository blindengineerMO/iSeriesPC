using Ipc.Console.Session;
using Ipc.Session;
using Ipc.Terminal;
using static Ipc.Core.Tests.DisplayPanelTests;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_buffers_preserve_prefix_renames_arrays_and_saved_database_descriptions(int ccsid)
    {
        Data("DESC");
        Compile("**free\ndcl-f DATA keyed qualified usage(*update:*output) extdesc('QGPL/DESC') prefix(P_);\ndcl-ds BUFFER qualified extname('QGPL/DESC':'ROW':*all) prefix(X_) inz;\nPARTS extfld('TEXT') dim(3);\nend-ds;\nchain 20 DATA BUFFER;\nif BUFFER.X_ID=101 and BUFFER.PARTS(1)='firs';\neval BUFFER.PARTS(2)='NEXT';\nupdate DATA.ROW BUFFER;\neval BUFFER.X_ID=999;\neval BUFFER.X_KEY=99;\nwrite DATA.ROW BUFFER;\nchain 99 DATA BUFFER;\nif BUFFER.X_ID=999 and BUFFER.PARTS(2)='NEXT';\nsndpgmmsg 'EXTNAME DISK OK';\nendif;\nendif;\nseton LR;");
        _files.RemoveMember("QGPL", "SOURCE", "APP"); _files.DeleteFile("QGPL", "DESC");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "EXTNAME DISK OK");
    }

    [Theory]
    [InlineData("", "read DATA BUFFER;")]
    [InlineData("", "write ROW BUFFER;")]
    [InlineData(":*input", "write ROW BUFFER;")]
    [InlineData(":*output", "read DATA BUFFER;")]
    [InlineData(":*key", "read DATA BUFFER;")]
    public void EXTNAME_result_io_requires_an_explicit_compatible_extract_before_creation(string extract, string operation)
    {
        _files.SaveSourceMember("QGPL", "SOURCE", "APP", "**free\ndcl-f DATA keyed usage(*update:*output);\ndcl-ds BUFFER extname('QGPL/DATA'" + extract + ") qualified inz;\nend-ds;\n" + operation, null);
        var result = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/APP) SRCFILE(QGPL/SOURCE)"); Assert.True(result.IsError); Assert.Contains("result", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(_system.Objects.Exists("QGPL", "APP", Ipc.Core.Objects.ObjectType.Program));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_unqualified_database_arrays_share_complete_fields_with_implicit_IO(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed usage(*update);\ndcl-ds INFO extname('QGPL/DATA') inz;\nTEXT extfld dim(3);\nend-ds;\nchain 20 DATA;\nif ID=101 and TEXT(1)='firs' and TEXT(2)='t';\neval TEXT(2)='NEXT';\nupdate ROW;\nchain 20 DATA;\nif TEXT(1)='firs' and TEXT(2)='NEXT';\nsndpgmmsg 'EXTERNAL DATABASE ALIASES OK';\nendif;\nendif;\nseton LR;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "EXTERNAL DATABASE ALIASES OK");
    }

    [Fact]
    public void EXTNAME_result_layouts_are_independent_of_a_compatible_logical_access_path()
    {
        var dds = string.Join('\n', LogicalFileTests.Line('R', "ROW", "PFILE(QGPL/DATA)"), LogicalFileTests.Line(' ', "ID"), LogicalFileTests.Line(' ', "KEY"), LogicalFileTests.Line(' ', "TEXT"), LogicalFileTests.Line('K', "ID"));
        var definition = new Ipc.Db.Dds.LogicalDdsCompiler(_ => (new("QGPL", "DATA"), _files.GetDefinition("QGPL", "DATA")!)).Compile("BYID", dds); _files.CreateLogicalFile("QGPL", "BYID", definition, dds);
        Compile("**free\ndcl-f BYID keyed;\ndcl-ds BUFFER qualified extname('QGPL/DATA':*all) end-ds;\nchain 101 BYID BUFFER;\nif BUFFER.ID=101 and BUFFER.TEXT='first';\nsndpgmmsg 'EXTERNAL LOGICAL FORMAT OK';\nendif;\nseton LR;");
        using var session = Session(); Ok(session.Execute("CALL QGPL/APP"), "EXTERNAL LOGICAL FORMAT OK");
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_KEY_structures_supply_nested_saved_key_searches(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed;\ndcl-ds MODEL qualified extname('QGPL/DATA':*key) template;\nend-ds;\ndcl-ds BOX qualified inz;\nKEYS likeds(MODEL);\nend-ds;\ndcl-ds BUFFER likerec(ROW);\neval BOX.KEYS.KEY=20;\nchain %kds(BOX.KEYS) DATA BUFFER;\nif BUFFER.ID=101;\nsndpgmmsg 'EXTNAME KEYS OK';\nendif;\nseton LR;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "EXTNAME KEYS OK");
    }
}

public sealed partial class RpgDisplayFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_display_buffers_use_independent_prefixes_and_saved_renamed_fields(int ccsid)
    {
        Display(Screen);
        Compile("**free\ndcl-f SCREEN workstn qualified prefix(P_);\ndcl-ds MODEL qualified extname('QGPL/SCREEN':'ENTRY':*all) prefix(X_) template;\nTARGET extfld('NAME');\nend-ds;\ndcl-ds BOX qualified inz;\nBUFFER likeds(MODEL);\nend-ds;\neval BOX.BUFFER.TARGET='EXTERNAL';\nexfmt SCREEN.ENTRY BOX.BUFFER;\nif BOX.BUFFER.TARGET='DONE' and BOX.BUFFER.X_AMOUNT=2.5;\nsndpgmmsg 'EXTNAME DISPLAY OK';\nendif;\nseton LR;");
        _sources.RemoveMember("QGPL", "SOURCE", "APP");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, new Terminal(d => {
            Assert.Contains("EXTERNAL", d.Session.Buffer.RowText(4)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd));
            foreach (var ch in "DONE") d.Session.Handle(new(AidKey.None, Character: ch)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.Tab)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd));
            foreach (var ch in "2.5") d.Session.Handle(new(AidKey.None, Character: ch)); return d.Session.Handle(new(AidKey.Enter)); }));
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("EXTNAME DISPLAY OK", result.Message);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_unqualified_display_fields_share_native_storage_with_implicit_record_IO(int ccsid)
    {
        Display(Screen); Compile("**free\ndcl-f SCREEN workstn;\ndcl-ds INFO extname('QGPL/SCREEN') inz;\nNAME extfld dim(2);\nend-ds;\neval NAME(1)='SHARE';\neval NAME(2)='D';\nexfmt ENTRY;\nif NAME(1)='DONE' and NAME(2)=*blanks and AMOUNT=2.5;\nsndpgmmsg 'EXTERNAL ALIASES OK';\nendif;\nseton LR;");
        using var session = new ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), _timeout.Token, new Terminal(d => {
            Assert.Contains("SHARED", d.Session.Buffer.RowText(4)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd)); foreach (var ch in "DONE") d.Session.Handle(new(AidKey.None, Character: ch));
            d.Session.Handle(new(AidKey.None, Edit: CursorEdit.Tab)); d.Session.Handle(new(AidKey.None, Edit: CursorEdit.FieldEraseToEnd)); foreach (var ch in "2.5") d.Session.Handle(new(AidKey.None, Character: ch)); return d.Session.Handle(new(AidKey.Enter)); }));
        var result = session.Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("EXTERNAL ALIASES OK", result.Message);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void EXTNAME_selects_the_first_or_explicit_display_format_without_opening_a_display(bool explicitFormat)
    {
        Display(Screen + Dds("OTHER", record: true) + Dds("TEXT", 6, usage: 'B', row: 2, column: 2));
        var field = explicitFormat ? "TEXT" : "NAME";
        Compile("**free\ndcl-c FORMAT 'OTHER';\ndcl-ds BUFFER qualified extname('QGPL/SCREEN'" + (explicitFormat ? ":FORMAT" : "") + ") inz;\nend-ds;\neval BUFFER." + field + "='FORMAT';\nsndpgmmsg BUFFER." + field + ";\nseton LR;");
        _sources.RemoveMember("QGPL", "SOURCE", "APP");
        var result = new CommandService(_system).Execute("CALL QGPL/APP"); Assert.False(result.IsError, result.Message); Assert.Contains("FORMAT", result.Message);
    }
}
