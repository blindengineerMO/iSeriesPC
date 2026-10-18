using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Db.Dds;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Rpg.Runtime;
using Ipc.Services.Events;
using Ipc.Services.Work;

namespace Ipc.Core.Tests;

public sealed partial class DatabaseNavigationTests
{
    private void SparseRelativeFile()
    {
        var format = new RecordFormat { Name = "RROW", Fields = [new() { Name = "ID", Type = FieldType.Binary, Length = 8 },
            new() { Name = "TEXT", Type = FieldType.Alpha, Length = 100 }] }; format.AssignPositions();
        _files.CreatePhysicalFile("QGPL", "RDATA", new() { Name = "RDATA", Attribute = FileAttribute.Physical, Formats = [format] }, "");
        foreach (var id in new long[] { 101, 102, 103, 108 })
            _files.Insert("QGPL", "RDATA", "RDATA", "RROW", new Dictionary<string, object?> { ["ID"] = id, ["TEXT"] = "row " + id });
        Sql("DELETE FROM \"QGPL.RDATA.RDATA\" WHERE ID=102; UPDATE \"QGPL.RDATA.RDATA\" SET rowid=8 WHERE ID=108");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Relative_lookup_preserves_the_cursors_configured_read_order(bool keyed)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed);
        Assert.True(cursor.ChainRecord(4)); Assert.Equal(4, Id(cursor.Current)); Assert.Equal(4L, cursor.CurrentRecordNumber);
        Assert.Equal(keyed ? 7 : 5, Id(cursor.Read())); Assert.Equal(keyed ? 7L : 5L, cursor.CurrentRecordNumber);
    }
    [Fact]
    public void Relative_bounds_find_physical_slots_before_resuming_keyed_reads()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        Assert.True(cursor.SetRecordLowerBound(5)); Assert.Null(cursor.Current); Assert.Null(cursor.CurrentRecordNumber);
        Assert.Equal(5, Id(cursor.Read())); // RRN 7 sorts before RRN 5 by the key
        cursor.SetRecordUpperBound(4); Assert.Equal(5, Id(cursor.Read()));
    }
    [Fact]
    public void Deleted_gaps_are_not_renumbered_and_bounds_skip_only_missing_slots()
    {
        SparseRelativeFile(); using var cursor = _files.OpenSequentialCursor("QGPL", "RDATA", "RDATA", keyed: false);
        Assert.True(cursor.ChainRecord(3)); Assert.Equal(103, Id(cursor.Current)); Assert.Equal(3L, cursor.CurrentRecordNumber);
        Assert.Equal(101, Id(cursor.ReadPrior())); Assert.Equal(103, Id(cursor.Read()));
        Assert.False(cursor.SetRecordLowerBound(2)); Assert.Equal(103, Id(cursor.Read()));
        cursor.SetRecordUpperBound(3); Assert.Equal(108, Id(cursor.Read())); Assert.Equal(8L, cursor.CurrentRecordNumber);
        Assert.False(cursor.ChainRecord(2)); Assert.Null(cursor.CurrentRecordNumber); Assert.Throws<CpfException>(() => cursor.Read());
    }
    [Fact]
    public void A_relative_limit_past_the_last_record_allows_a_prior_read_from_EOF()
    {
        SparseRelativeFile(); using var cursor = _files.OpenSequentialCursor("QGPL", "RDATA", "RDATA", keyed: false);
        Assert.False(cursor.SetRecordLowerBound(9)); Assert.True(cursor.AtEnd); Assert.Null(cursor.Read());
        Assert.Equal(108, Id(cursor.ReadPrior())); cursor.SetRecordUpperBound(8); Assert.True(cursor.AtEnd);
    }
    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4294967289L)]
    [InlineData(long.MaxValue)]
    public void Invalid_relative_numbers_do_not_change_position_or_records(long number)
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(1));
        Assert.Throws<CpfException>(() => cursor.ChainRecord(number)); Assert.Throws<CpfException>(() => cursor.SetRecordLowerBound(number));
        Assert.Throws<CpfException>(() => cursor.SetRecordUpperBound(number)); Assert.Throws<CpfException>(() => cursor.DeleteRecord(number));
        Assert.Equal(1L, cursor.CurrentRecordNumber); Assert.Equal(2, Id(cursor.Read())); Assert.Equal(8, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Fact]
    public void The_native_maximum_RRN_is_an_integer_lookup_without_allocating_intervening_slots()
    {
        SparseRelativeFile(); Sql("UPDATE \"QGPL.RDATA.RDATA\" SET rowid=4294967288 WHERE ID=108");
        using var cursor = _files.OpenSequentialCursor("QGPL", "RDATA", "RDATA", keyed: false);
        Assert.True(cursor.ChainRecord(DatabaseRecordCursor.MaximumRelativeRecordNumber)); Assert.Equal(108, Id(cursor.Current));
        Assert.Equal(DatabaseRecordCursor.MaximumRelativeRecordNumber, cursor.CurrentRecordNumber); Assert.Equal(3, _files.RowCount("QGPL", "RDATA", "RDATA"));
        Assert.True(cursor.DeleteRecord(DatabaseRecordCursor.MaximumRelativeRecordNumber)); Assert.Null(cursor.CurrentRecordNumber);
    }
    [Fact]
    public void Relative_deletion_preserves_duplicate_keys_and_the_position_on_a_miss()
    {
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false); Assert.True(cursor.ChainRecord(1));
        Assert.False(cursor.DeleteRecord(99)); Assert.Equal(1L, cursor.CurrentRecordNumber);
        Assert.True(cursor.DeleteRecord(2)); Assert.Equal("row 1", Stored(1)["TEXT"]); Assert.Equal(3, Id(cursor.Read()));
        Assert.Equal(7, _files.RowCount("QGPL", "NAV", "NAV"));
    }
    [Fact]
    public void Relative_access_respects_the_opened_member_and_logical_selection()
    {
        _files.AddMember("QGPL", "NAV", "OTHER");
        _files.Insert("QGPL", "NAV", "OTHER", "REC", new Dictionary<string, object?> { ["ID"] = 100L, ["AMOUNT"] = 1m, ["LABEL"] = "A", ["TEXT"] = "other" });
        using (var other = _files.OpenSequentialCursor("QGPL", "NAV", "OTHER", keyed: false))
        { Assert.True(other.ChainRecord(1)); Assert.Equal(100, Id(other.Current)); }
        var dds = string.Join('\n', LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)"), LogicalFileTests.Line('S', "ID", "COMP(GT 4)"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("RVIEW", dds);
        _files.CreateLogicalFile("QGPL", "RVIEW", definition, dds, ["NAV"]);
        using var cursor = _files.OpenSequentialCursor("QGPL", "RVIEW", "RVIEW", keyed: false);
        Assert.False(cursor.ChainRecord(4)); Assert.False(cursor.SetRecordLowerBound(4)); Assert.Equal(5, Id(cursor.Read()));
        Assert.False(cursor.DeleteRecord(4)); Assert.True(cursor.DeleteRecord(5)); Assert.Equal("row 4", Stored(4)["TEXT"]);
    }
    [Fact]
    public void Ambiguous_logical_member_RRNs_are_rejected_without_losing_the_keyed_position()
    {
        _files.AddMember("QGPL", "NAV", "OTHER");
        var dds = string.Join('\n', LogicalFileTests.Line('R', "VREC", "PFILE(QGPL/NAV)"), LogicalFileTests.Line('K', "AMOUNT"), LogicalFileTests.Line('K', "LABEL"));
        var definition = new LogicalDdsCompiler(_ => (new("QGPL", "NAV"), _files.GetDefinition("QGPL", "NAV")!)).Compile("RMULTI", dds);
        _files.CreateLogicalFile("QGPL", "RMULTI", definition, dds, ["NAV", "OTHER"]);
        using var cursor = _files.OpenSequentialCursor("QGPL", "RMULTI", "RMULTI"); Assert.Equal(1, Id(cursor.Read()));
        Assert.Throws<CpfException>(() => cursor.ChainRecord(1)); Assert.Throws<CpfException>(() => cursor.SetRecordLowerBound(1));
        Assert.Throws<CpfException>(() => cursor.SetRecordUpperBound(1)); Assert.Throws<CpfException>(() => cursor.DeleteRecord(1)); Assert.Equal(2, Id(cursor.Read()));
    }
    [Fact]
    public void Relative_limits_do_not_decode_nonkey_payloads_and_lookups_do_not_scan_other_payloads()
    {
        Sql("UPDATE \"QGPL.NAV.NAV\" SET ID=NULL WHERE rowid=4");
        using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false);
        Assert.True(cursor.SetRecordLowerBound(4)); Assert.Throws<CpfException>(() => cursor.Read());
        Assert.True(cursor.ChainRecord(1)); Assert.Equal(1, Id(cursor.Current));
    }
    [Fact]
    public void Relative_reads_and_writes_honor_another_jobs_allocation_of_that_slot()
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RelativeRecordFixture32");
        var first = _system.Jobs.CreateInteractive("QSECOFR"); var second = _system.Jobs.CreateInteractive("QSECOFR");
        var locks = new JobLockStore(_system.Connections); IDisposable blocked;
        using (OperationIdentity.Enter("QSECOFR", second.Key))
            blocked = locks.Acquire(second.Key, new("QGPL", "NAV", ObjectType.File, "NAV", 3), JobLockMode.Exclusive, TimeSpan.Zero);
        using (blocked)
        using (OperationIdentity.Enter("QSECOFR", first.Key))
        using (locks.EnterCommand(first.Key))
        using (var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", keyed: false))
        {
            Assert.True(cursor.ChainRecord(1));
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => cursor.ChainRecord(3)).MessageId);
            Assert.Equal("CPF1002", Assert.Throws<CpfException>(() => cursor.DeleteRecord(3)).MessageId);
            Assert.Equal(1L, cursor.CurrentRecordNumber);
        }
    }
    [Fact]
    public void Relative_delete_requires_delete_authority_even_for_a_missing_slot()
    {
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "QUSER", Authorities.UseBits);
        using var identity = OperationIdentity.Enter("QUSER"); using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV");
        Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.DeleteRecord(100)).MessageId);
    }
    [Fact]
    public void Relative_operations_honor_cursor_cancellation()
    {
        using var stop = new CancellationTokenSource(); using var cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV", cancellationToken: stop.Token);
        stop.Cancel(); Assert.Throws<OperationCanceledException>(() => cursor.ChainRecord(1)); Assert.Throws<OperationCanceledException>(() => cursor.SetRecordLowerBound(1));
        Assert.Throws<OperationCanceledException>(() => cursor.DeleteRecord(1));
    }
    [Theory]
    [InlineData("chain 3 RDATA;", 103)]
    [InlineData("chain 3 RDATA;\nreadp RDATA;", 101)]
    [InlineData("setll 2 RDATA;\nread RDATA;", 103)]
    [InlineData("setgt 3 RDATA;\nread RDATA;", 108)]
    [InlineData("chain 3 RDATA;\nread RDATA;", 108)]
    [InlineData("delete 3 RDATA;\nread RDATA;", 108)]
    public void Compiled_unkeyed_RPG_uses_physical_RRNs_instead_of_row_indices_or_ID_values(string body, int expected)
    {
        SparseRelativeFile(); Assert.Equal(expected, RunKey(body));
    }
    [Fact]
    public void Compiled_relative_input_uses_a_zero_decimal_field_and_can_update_the_selected_record()
    {
        SparseRelativeFile();
        var context = RunKeySource("**free\ndcl-s RRN int(20) inz(3);\ndcl-s ID int(20);\ndcl-s TEXT char(100);\nchain RRN RDATA;\neval TEXT='changed';\nupdate RDATA;\nread RDATA;\nreturn;");
        Assert.Equal(108m, RpgValues.ToDecimal(context.ReadValue("ID")));
        Assert.Equal("changed", (_files.ReadAll("QGPL", "RDATA", "RDATA").Single(row => (long)row["ID"]! == 103)["TEXT"] as string)?.TrimEnd());
    }
    [Fact]
    public void A_missing_relative_chain_leaves_RPG_fields_unchanged_and_sets_FOUND_false()
    {
        SparseRelativeFile();
        var context = RunKeySource("**free\ndcl-s ID int(20) inz(777);\ndcl-s FOUND ind;\nchain 2 RDATA;\neval FOUND=%found(RDATA);\nreturn;");
        Assert.Equal(777m, RpgValues.ToDecimal(context.ReadValue("ID"))); Assert.False(RpgValues.ToBool(context.ReadValue("FOUND")));
    }
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4294967289")]
    [InlineData("3.0")]
    [InlineData("'3'")]
    [InlineData("(3)")]
    public void Invalid_RPG_relative_arguments_are_not_coerced_or_truncated(string argument)
    {
        SparseRelativeFile(); Assert.Throws<RpgRuntimeException>(() => RunKey("chain " + argument + " RDATA;"));
        Assert.Equal(3, _files.RowCount("QGPL", "RDATA", "RDATA"));
    }
    [Fact]
    public void A_numeric_RRN_field_with_fractional_declared_scale_is_rejected_even_for_an_integral_value()
    {
        SparseRelativeFile(); Assert.Throws<RpgRuntimeException>(() => RunKeySource("**free\ndcl-s RRN packed(5:2) inz(3);\nchain RRN RDATA;\nreturn;"));
    }
    [Fact]
    public void Compiled_RPG_can_address_the_maximum_RRN_without_signed_32_bit_wraparound()
    {
        SparseRelativeFile(); Sql("UPDATE \"QGPL.RDATA.RDATA\" SET rowid=4294967288 WHERE ID=108");
        Assert.Equal(108, RunKey("chain 4294967288 RDATA;"));
    }
    [Fact]
    public void An_indexed_numeric_array_element_can_supply_the_RRN()
    {
        SparseRelativeFile(); var source = string.Join('\n', RpgFileKeyCompilerTests.D("RRNS", "S", 3, 0, "DIM(2)"), RpgFileKeyCompilerTests.D("ID", "I", 8),
            "/free", "eval RRNS(2)=3;", "chain RRNS(2) RDATA;", "return;", "/end-free");
        Assert.Equal(103m, RpgValues.ToDecimal(RunKeySource(source).ReadValue("ID")));
    }
    [Fact]
    public void Indexing_a_scalar_RRN_field_is_not_treated_as_reading_the_whole_field()
    {
        SparseRelativeFile(); Assert.Throws<RpgRuntimeException>(() => RunKeySource("**free\ndcl-s RRN int(20) inz(3);\nchain RRN(2) RDATA;\nreturn;"));
    }
    [Fact]
    public void A_host_can_select_arrival_access_for_a_physical_file_that_has_DDS_keys()
    {
        var access = new RpgSqliteFileAccess(_files);
        var host = new RpgHost { Files = access, OpenFile = _ => {
            var cursor = new RpgFileCursor(access, "QGPL", "NAV", "NAV", keyed: false);
            Assert.False(cursor.IsKeyed); return new RpgFileHandle(() => cursor, cursor.Dispose);
        } };
        var context = new RpgInterpreter(host).Run(Ipc.Rpg.Parsing.RpgCompiler.Compile("QGPL", "ARRIVAL", "**free\ndcl-s ID int(20);\nchain 3 NAV;\nread NAV;\nreturn;"));
        Assert.Equal(4m, RpgValues.ToDecimal(context.ReadValue("ID")));
    }
    [Fact]
    public void Explicit_keyed_access_to_an_unkeyed_file_is_rejected()
    {
        SparseRelativeFile(); Assert.Throws<RpgRuntimeException>(() => new RpgFileCursor(new RpgSqliteFileAccess(_files), "QGPL", "RDATA", "RDATA", keyed: true));
    }
    [Fact]
    public void Relative_operations_reject_a_foreign_execution_context()
    {
        _system.Security.Authority.Grant("QGPL", "NAV", ObjectType.File, "QUSER", Authorities.ChangeBits);
        DatabaseRecordCursor cursor;
        using (OperationIdentity.Enter("QUSER")) { cursor = _files.OpenSequentialCursor("QGPL", "NAV", "NAV"); Assert.True(cursor.ChainRecord(1)); }
        using (cursor)
        {
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.ChainRecord(1)).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.SetRecordLowerBound(1)).MessageId);
            Assert.Equal("CPF9802", Assert.Throws<CpfException>(() => cursor.DeleteRecord(1)).MessageId);
        }
    }
    [Fact]
    public void A_legacy_provider_cannot_guess_RRNs_from_its_materialized_row_list()
    {
        var access = new LegacyUnkeyedAccess();
        using var cursor = new RpgFileCursor(access, "QGPL", "LEGACY", "LEGACY");
        Assert.Throws<RpgRuntimeException>(() => cursor.ChainRecord(1)); Assert.Throws<RpgRuntimeException>(() => cursor.SetRecordLowerBound(1));
        Assert.Throws<RpgRuntimeException>(() => cursor.DeleteRecord(1)); Assert.Equal(2, access.Rows.Count);
    }
    [Fact]
    public void Relative_limits_do_not_replace_RPG_input_fields_and_a_delete_miss_preserves_position()
    {
        SparseRelativeFile();
        var context = RunKeySource("**free\ndcl-s ID int(20) inz(777);\nsetll 3 RDATA;\nreturn;");
        Assert.Equal(777m, RpgValues.ToDecimal(context.ReadValue("ID")));
        Assert.Equal(103, RunKey("read RDATA;\ndelete 2 RDATA;\nread RDATA;"));
    }
}
