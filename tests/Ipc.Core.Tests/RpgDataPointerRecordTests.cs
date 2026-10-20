using System.Globalization;

namespace Ipc.Core.Tests;

public sealed partial class RpgDiskFileTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Based_views_of_imported_external_fields_update_real_database_records(int ccsid)
    {
        Compile("**free\ndcl-f DATA keyed usage(*update);\ndcl-s P pointer;\ndcl-s AMOUNT packed(9:2) based(P);\ndcl-s Q pointer;\ndcl-s WORD char(12) based(Q);\nchain 20 DATA;\neval P=%addr(KEY);\neval Q=GETTEXT();\neval AMOUNT=44.25;\neval WORD='address';\nupdate ROW;\nsndpgmmsg %char(KEY)+':'+%trim(TEXT);\nseton LR;\ndcl-proc GETTEXT;\ndcl-pi *n pointer end-pi;\nreturn %addr(TEXT);\nend-proc;");
        using var session = Session(ccsid); Ok(session.Execute("CALL QGPL/APP"), "44.25:address");
        var row = _files.ReadAll("QGPL", "DATA", "DATA").Single(r => Convert.ToInt64(r["ID"], CultureInfo.InvariantCulture) == 101L);
        Assert.Equal(44.25m, Convert.ToDecimal(row["KEY"], CultureInfo.InvariantCulture)); Assert.Equal("address", row["TEXT"]?.ToString()?.TrimEnd());
    }
}
