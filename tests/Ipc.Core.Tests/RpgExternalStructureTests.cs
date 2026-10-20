using System.Text.Json.Nodes;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgExternalStructureTests
{
    private static RpgExternalLayout Describe(RpgExternalRequest request) => new("QGPL/DESC", request.Format ?? "ROW", "DISK", 26,
        [new("CUST", RpgFieldKind.Character, 6, 0, 6, 1, DefaultValue: "OLD"), new("ID", RpgFieldKind.Integer, 10, 0, 4, 7, DefaultValue: "5"),
         new("PRICE", RpgFieldKind.Packed, 7, 2, 4, 11, 1, DefaultValue: "12.34"), new("ARR", RpgFieldKind.Character, 12, 0, 12, 15)]);
    private static RpgProgram Compile(string text, Func<RpgExternalRequest, RpgExternalLayout>? resolver = null)
        => RpgCompiler.Compile("QGPL", "EXTDS", "**free\n" + text, externalResolver: resolver ?? Describe);
    private static RpgRuntimeContext Run(string text, int ccsid = 37) => new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(Compile(text));
    private static object? Read(RpgRuntimeContext context, string text) => RpgExpressionParser.Parse(text).Eval(context);

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTNAME_supplies_native_fields_without_any_runtime_file_declaration(int ccsid)
    {
        var program = Compile("dcl-ds BOX extname('QGPL/DESC') qualified inz;\nend-ds;\neval BOX.CUST='HELLO';\neval BOX.ID=7;\neval BOX.PRICE=12.34;\nreturn;");
        Assert.Empty(program.DiskFiles); Assert.Empty(program.Workstations); Assert.Empty(program.FileSpecs);
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(program);
        Assert.Equal("HELLO ", Read(context, "BOX.CUST")); Assert.Equal(7L, Read(context, "BOX.ID")); Assert.Equal(12.34m, Read(context, "BOX.PRICE")); Assert.Equal(26, Assert.IsType<ProgramBuffer>(Read(context, "BOX")).Length);
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXT_and_unqualified_external_fields_use_the_structure_name_and_default_values(int ccsid)
    {
        var context = Run("dcl-ds DESC ext inz(*extdft);\nend-ds;\neval ID=ID+1;\nreturn;", ccsid);
        Assert.Equal(6L, context.ReadValue("ID")); Assert.Equal("OLD   ", context.ReadValue("CUST")); Assert.Equal(12.34m, context.ReadValue("PRICE"));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void EXTFLD_renames_bypass_PREFIX_and_DIM_divides_the_original_field_storage(int ccsid)
    {
        var context = Run("dcl-c CUST_NAME 'CUST';\ndcl-ds BOX qualified extname('QGPL/DESC') prefix('p_') inz;\nCUSTOMER extfld(CUST_NAME) inz('ALICE');\nITEMS extfld('ARR') dim(3) inz('AB');\nend-ds;\neval BOX.ITEMS(2)='NEXT';\nreturn;", ccsid);
        Assert.Equal("ALICE ", Read(context, "BOX.CUSTOMER")); Assert.Equal(0L, Read(context, "BOX.P_ID"));
        Assert.Equal(new object?[] { "AB  ", "NEXT", "AB  " }, Read(context, "BOX.ITEMS")); Assert.Equal(26, Assert.IsType<ProgramBuffer>(Read(context, "BOX")).Length);
    }

    [Fact]
    public void Additional_subfields_follow_external_storage_and_LIKEDS_keeps_recursive_defaults()
    {
        var context = Run("dcl-ds MODEL extname('QGPL/DESC') qualified template inz(*extdft);\nEXTRA int(10) inz(9);\nend-ds;\ndcl-ds COPY likeds(MODEL) inz(*likeds);\nreturn;");
        Assert.Equal(9L, Read(context, "COPY.EXTRA")); Assert.Equal(5L, Read(context, "COPY.ID")); Assert.Equal(30, Assert.IsType<ProgramBuffer>(Read(context, "COPY")).Length);
    }

    [Fact]
    public void Forward_external_members_parameters_and_returns_resolve_one_description_snapshot()
    {
        var count = 0; var program = Compile("dcl-s N like(MODEL.ID);\ndcl-ds RESULT likeds(MODEL);\ndcl-ds MODEL qualified extname('QGPL/DESC') template;\nend-ds;\neval RESULT=MAKE();\neval N=RESULT.ID;\nreturn;\ndcl-proc MAKE;\ndcl-pi *n likeds(MODEL) end-pi;\ndcl-ds LOCAL likeds(MODEL) inz;\neval LOCAL.ID=12;\nreturn LOCAL;\nend-proc;", request => { count++; return Describe(request); });
        var context = new RpgInterpreter(new()).Run(program); Assert.Equal(12L, context.ReadValue("N")); Assert.Equal(1, count);
        var bindings = RpgExternalBindings.Restore(program.Source!.Text, RpgExternalBindings.Serialize(program));
        var restored = RpgCompiler.Compile("QGPL", "EXTDS", program.Source.Text, externalResolver: bindings.Resolve); bindings.Verify(restored);
        Assert.Equal(12L, new RpgInterpreter(new()).Run(restored).ReadValue("N"));
    }

    [Fact]
    public void KEY_extracts_use_key_order_and_compact_native_widths()
    {
        var context = Run("dcl-ds KEYS qualified extname('QGPL/DESC':*key) inz;\nend-ds;\neval KEYS.PRICE=2.5;\nreturn;");
        Assert.Equal(4, Assert.IsType<ProgramBuffer>(Read(context, "KEYS")).Length); Assert.Equal(2.5m, Read(context, "KEYS.PRICE"));
    }
    [Theory]
    [InlineData(0)] [InlineData(1)]
    public void Traditional_external_headers_and_subfields_preserve_both_column_origins(int shift)
    {
        var source = RpgFixedStructureTests.D("BOX", "DS", keywords: "EXTNAME(DESC) QUALIFIED INZ", external: 'E', shift: shift) +
            RpgFixedStructureTests.D("CUSTOMER", keywords: "EXTFLD(CUST) INZ('FIXED')", external: 'E', shift: shift) + RpgFixedCalculationTests.C("RETURN");
        var program = RpgCompiler.Compile("QGPL", "EXTDS", source, externalResolver: Describe);
        Assert.Equal("FIXED ", Read(new RpgInterpreter(new()).Run(program), "BOX.CUSTOMER"));
    }

    [Fact]
    public void Aliases_of_one_external_format_share_EXACT_type_ancestry_after_restore()
    {
        var program = Compile("dcl-ds MODEL qualified extname('DESC') template;\nend-ds;\ndcl-ds OTHER qualified extname('QGPL/DESC':'ROW') inz;\nend-ds;\ncallp CHANGE(OTHER);\nreturn;\ndcl-proc CHANGE;\ndcl-pi *n;\nP likeds(MODEL) options(*exact);\nend-pi;\neval P.ID=15;\nend-proc;");
        var saved = RpgExternalBindings.Restore(program.Source!.Text, RpgExternalBindings.Serialize(program));
        var restored = RpgCompiler.Compile("QGPL", "EXTDS", program.Source.Text, externalResolver: saved.Resolve); saved.Verify(restored);
        Assert.Equal(15L, Read(new RpgInterpreter(new()).Run(restored), "OTHER.ID"));
    }

    [Fact]
    public void Local_STATIC_external_structures_retain_values_without_retaining_a_file()
    {
        var program = Compile("dcl-s RESULT int(10);\neval RESULT=GET();\neval RESULT=GET();\nreturn;\ndcl-proc GET;\ndcl-pi *n int(10) end-pi;\ndcl-ds BOX qualified extname('QGPL/DESC') static inz(*extdft);\nend-ds;\neval BOX.ID=BOX.ID+1;\nreturn BOX.ID;\nend-proc;");
        var saved = RpgExternalBindings.Restore(program.Source!.Text, RpgExternalBindings.Serialize(program)); var restored = RpgCompiler.Compile("QGPL", "EXTDS", program.Source.Text, externalResolver: saved.Resolve); saved.Verify(restored);
        Assert.Equal(7L, new RpgInterpreter(new()).Run(restored).ReadValue("RESULT")); Assert.Empty(restored.DiskFiles);
    }

    [Fact]
    public void Current_datetime_defaults_are_evaluated_by_the_running_module()
    {
        var program = Compile("dcl-ds BOX qualified extname('QGPL/CLOCK') inz(*extdft);\nend-ds;\nreturn;", _ => new("QGPL/CLOCK", "ROW", "DISK", 44, [new("TODAY", RpgFieldKind.Date, 10, 0, 10, 1, CurrentDatetimeDefault: true), new("CLOCK", RpgFieldKind.Time, 8, 0, 8, 11, CurrentDatetimeDefault: true), new("STAMP", RpgFieldKind.Timestamp, 26, 0, 26, 19, CurrentDatetimeDefault: true)]));
        var context = new RpgInterpreter(new() { Now = new DateTimeOffset(2031, 3, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234567) }).Run(program);
        Assert.Equal(new DateTimeOffset(2031, 3, 4, 0, 0, 0, TimeSpan.Zero), Read(context, "BOX.TODAY"));
        Assert.Equal(new TimeSpan(12, 34, 56), Read(context, "BOX.CLOCK")); Assert.Equal(new DateTimeOffset(2031, 3, 4, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560), Read(context, "BOX.STAMP"));
    }

    [Theory]
    [InlineData("dcl-ds BOX qualified ext extname('DESC');\nend-ds;")]
    [InlineData("dcl-ds BOX ext(1);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC':*null:*null);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC':'ROW':*all:*null:*null);\nend-ds;")]
    [InlineData("dcl-s FILE char(4) inz('DESC');\ndcl-ds BOX extname(FILE);\nend-ds;")]
    [InlineData("dcl-ds BOX extname(FILE);\nend-ds;\ndcl-c FILE 'DESC';")]
    [InlineData("dcl-ds BOX extname('DESC') likeds(MISSING);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nBAD extfld('MISSING');\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nID extfld('CUST');\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nONE extfld('CUST');\nTWO extfld('CUST');\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nARR extfld dim(5);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nARR extfld dim(0);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC');\nID extfld inz(1.5);\nend-ds;")]
    [InlineData("dcl-ds BOX extname('DESC') prefix(P_:50);\nend-ds;")]
    public void Invalid_external_declarations_fail_with_source_locations(string source)
    {
        var error = Assert.Throws<RpgCompileException>(() => Compile(source)); Assert.NotNull(error.Location);
    }

    [Fact]
    public void External_defaults_initialize_the_original_field_before_DIM_interprets_its_bytes()
    {
        var context = Run("dcl-ds MODEL qualified extname('DESC') template inz(*extdft);\nPARTS extfld('CUST') dim(2);\nend-ds;\ndcl-ds COPY likeds(MODEL) inz(*likeds);\nreturn;");
        Assert.Equal(new object?[] { "OLD", "   " }, Read(context, "COPY.PARTS"));
    }

    [Fact]
    public void External_date_time_literals_and_overrides_keep_their_native_representations()
    {
        var program = Compile("dcl-ds BOX qualified extname('CLOCK') inz(*extdft);\nDAY extfld('TODAY') inz('2020-02-03');\nend-ds;\nreturn;", _ => new("QGPL/CLOCK", "ROW", "DISK", 44,
            [new("TODAY", RpgFieldKind.Date, 10, 0, 10, 1, DefaultValue: "2012-01-02"), new("CLOCK", RpgFieldKind.Time, 8, 0, 8, 11, DefaultValue: "12:34:56"),
             new("STAMP", RpgFieldKind.Timestamp, 26, 0, 26, 19, DefaultValue: "2012-01-02T12:34:56Z")]));
        var context = new RpgInterpreter(new()).Run(program);
        Assert.Equal(new DateTimeOffset(2020, 2, 3, 0, 0, 0, TimeSpan.Zero), Read(context, "BOX.DAY")); Assert.Equal(new TimeSpan(12, 34, 56), Read(context, "BOX.CLOCK"));
        Assert.Equal(new DateTimeOffset(2012, 1, 2, 12, 34, 56, TimeSpan.Zero), Read(context, "BOX.STAMP"));
    }

    [Theory]
    [InlineData("version")] [InlineData("duplicate")] [InlineData("unknown")] [InlineData("width")]
    public void Saved_external_descriptions_reject_malformed_or_unsupported_metadata(string defect)
    {
        var program = Compile("dcl-ds BOX extname('DESC');\nend-ds;\nreturn;"); var text = RpgExternalBindings.Serialize(program); var json = JsonNode.Parse(text)!;
        switch (defect)
        {
            case "version": json["version"] = 3; break;
            case "duplicate": text = text.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal); break;
            case "unknown": json["extra"] = true; break;
            case "width": json["descriptions"]![0]!["layout"]!["fields"]![1]!["storageLength"] = -1; break;
        }
        Assert.Throws<InvalidDataException>(() => RpgExternalBindings.Restore(program.Source!.Text, defect == "duplicate" ? text : json.ToJsonString()));
    }

    [Fact]
    public void Saved_external_descriptions_check_source_hash_and_every_requested_binding()
    {
        var program = Compile("dcl-ds BOX extname('DESC');\nend-ds;\nreturn;"); var text = RpgExternalBindings.Serialize(program);
        Assert.Throws<InvalidDataException>(() => RpgExternalBindings.Restore(program.Source!.Text + "\n", text));
        var json = JsonNode.Parse(text)!; var extra = json["descriptions"]![0]!.DeepClone(); extra["request"]!["file"] = "OTHER"; json["descriptions"]!.AsArray().Add(extra);
        var saved = RpgExternalBindings.Restore(program.Source!.Text, json.ToJsonString()); var restored = RpgCompiler.Compile("QGPL", "EXTDS", program.Source.Text, externalResolver: saved.Resolve);
        Assert.Throws<InvalidDataException>(() => saved.Verify(restored));
    }

    [Fact]
    public void External_metadata_serialization_enforces_its_bound_while_writing_escaped_defaults()
    {
        var fields = Enumerable.Range(1, 32).Select(n => new RpgExternalField("F" + n, RpgFieldKind.Character, 32767, 0, 32767, (n - 1) * 32767 + 1, DefaultValue: new string('\u0001', 32767))).ToArray();
        var program = Compile("dcl-ds BOX extname('DESC') template;\nend-ds;\nreturn;", _ => new("QGPL/DESC", "ROW", "DISK", 32 * 32767, fields));
        Assert.Throws<InvalidDataException>(() => RpgExternalBindings.Serialize(program));
    }

    [Fact]
    public void External_description_count_is_bounded_across_main_and_procedure_scopes()
    {
        var main = string.Concat(Enumerable.Range(1, 254).Select(n => "dcl-ds T" + n + " extname('F" + n + "') template;\nend-ds;\n"));
        var source = main + "return;\ndcl-proc FIRST;\ndcl-pi *n end-pi;\ndcl-ds ONE extname('F255') template;\nend-ds;\ndcl-ds TWO extname('F256') template;\nend-ds;\nend-proc;\ndcl-proc SECOND;\ndcl-pi *n end-pi;\ndcl-ds ONE extname('F257') template;\nend-ds;\nend-proc;";
        var error = Assert.Throws<RpgCompileException>(() => Compile(source)); Assert.Contains("256 external", error.Message); Assert.NotNull(error.Location);
    }

    [Fact]
    public void Missing_external_resolvers_report_the_original_declaration_location()
    {
        var error = Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "EXTDS", "**free\ndcl-ds BOX extname('DESC');\nend-ds;")); Assert.NotNull(error.Location);
    }

    [Fact]
    public void Inline_END_DS_preserves_external_layouts_and_following_declarations()
    {
        var context = Run("dcl-ds BOX extname('DESC') qualified inz(*extdft) end-ds;\ndcl-ds PAD len(8) end-ds;\ndcl-s N like(BOX.ID);\neval N=BOX.ID;\nreturn;");
        Assert.Equal(5L, context.ReadValue("N")); Assert.Equal(8, Assert.IsType<ProgramBuffer>(Read(context, "PAD")).Length);
    }

    [Fact]
    public void External_float_and_indicator_defaults_use_their_native_scalar_codecs()
    {
        var program = Compile("dcl-ds BOX qualified extname('VALUES') inz(*extdft);\nend-ds;\nreturn;", _ => new("QGPL/VALUES", "ROW", "DISK", 13,
            [new("SINGLE", RpgFieldKind.Float, 4, 0, 4, 1, DefaultValue: "1.25"), new("DOUBLE", RpgFieldKind.Float, 8, 0, 8, 5, DefaultValue: "2.5"),
             new("FLAG", RpgFieldKind.Indicator, 1, 0, 1, 13, DefaultValue: "1")]));
        var context = new RpgInterpreter(new()).Run(program); Assert.Equal(1.25f, Read(context, "BOX.SINGLE")); Assert.Equal(2.5d, Read(context, "BOX.DOUBLE")); Assert.Equal(true, Read(context, "BOX.FLAG"));
    }

}
