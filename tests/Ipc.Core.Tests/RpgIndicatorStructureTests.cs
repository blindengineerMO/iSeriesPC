using System.Text.Json.Nodes;
using Ipc.Core.Work;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgIndicatorStructureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Declaration_constants_observe_final_INDDS_INFDS_and_LIKEDS_lengths(bool filesFirst)
    {
        var file = "dcl-f SCREEN workstn usropn indds(AREA) infds(FEEDBACK);\n";
        var declarations = Area + "dcl-ds FEEDBACK qualified;\nCODE *status;\nend-ds;\n" +
            "dcl-ds COPY likeds(AREA);\ndcl-c AREA_SIZE %size(AREA);\ndcl-c COPY_SIZE %size(COPY);\n" +
            "dcl-c INFO_SIZE %size(FEEDBACK);\ndcl-ds RECEIVER qualified len(AREA_SIZE);\n" +
            "TEXT char(INFO_SIZE);\nend-ds;\n";
        var context = new RpgInterpreter(new()).Run(Compile((filesFirst ? file : "") + declarations + (filesFirst ? "" : file) + "return;"));
        Assert.Equal(99m, context.ReadValue("AREA_SIZE")); Assert.Equal(99m, context.ReadValue("COPY_SIZE"));
        Assert.Equal(80m, context.ReadValue("INFO_SIZE")); Assert.Equal(99, Read(context, "%size(RECEIVER)"));
        Assert.Equal(80, Read(context, "%len(RECEIVER.TEXT)"));
    }
    [Fact]
    public void Layout_constant_discovery_does_not_mask_an_invalid_explicit_INDDS_length()
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" +
            Area.Replace("qualified;", "qualified len(98);") + "dcl-c WIDTH %size(AREA);\nreturn;"));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Multiple_occurrence_INDDS_uses_the_current_bank_for_conditioning_and_responses(int ccsid)
    {
        var calls = 0;
        var provider = new Workstation((_, flags) =>
        {
            calls++; Assert.Equal(calls == 2, flags[21]); Assert.False(flags[3]); return Response((3, true));
        });
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, OpenWorkstation = _ => provider }).Run(
            Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area.Replace("qualified;", "qualified occurs(2);") +
                "write ENTRY;\neval %occur(AREA)=2;\neval AREA.SHOW=*on;\nexfmt ENTRY;\n" +
                "eval %occur(AREA)=1;\nreturn;"));
        Assert.Equal(198, Read(context, "%size(AREA:*all)")); Assert.Equal(99, Read(context, "%size(AREA)"));
        Assert.Equal(true, Read(context, "AREA.EXIT")); Assert.Equal(false, Read(context, "AREA.SHOW"));
        Assert.False(context.Indicators[3]); Assert.False(context.Indicators[21]); Assert.Equal(2, calls);
    }
    [Fact]
    public void Pending_INDDS_response_updates_the_bank_captured_at_operation_entry()
    {
        RpgRuntimeContext? context = null;
        var provider = new Workstation((_, flags) =>
        {
            Assert.True(flags[21]);
            // A provider callback changes selection while the transfer is pending.
            context!.ClearValue("AREA", true); return Response((3, true));
        });
        var program = Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area.Replace("qualified;", "qualified occurs(2);") +
            "dcl-s FIRST ind;\neval %occur(AREA)=2;\neval AREA.SHOW=*on;\nexfmt ENTRY;\n" +
            "eval FIRST=AREA.EXIT;\neval %occur(AREA)=2;\nreturn;");
        var host = new RpgHost { OpenWorkstation = _ => provider }; context = new(program, host);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(false, context.ReadValue("FIRST")); Assert.Equal(true, Read(context, "AREA.EXIT"));
    }
    [Fact]
    public void CLEAR_ALL_resets_every_indicator_occurrence_and_selects_the_first()
    {
        var context = new RpgInterpreter(new()).Run(Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" +
            Area.Replace("qualified;", "qualified occurs(2);") + "eval AREA.SHOW=*on;\neval %occur(AREA)=2;\n" +
            "eval AREA.EXIT=*on;\nclear *all AREA;\nreturn;"));
        Assert.Equal(1, Read(context, "%occur(AREA)")); Assert.All(Bytes(context), b => Assert.Equal(0xF0, b));
    }
    [Fact]
    public void Final_INDDS_expansion_applies_the_storage_bound_to_all_occurrences()
    {
        Assert.Throws<RpgCompileException>(() => Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" +
            Area.Replace("qualified;", "qualified occurs(32767);")));
    }
    private const string Area = "dcl-ds AREA qualified;\nEXIT ind pos(3);\nSHOW ind pos(21);\nFLAGS ind dim(3) pos(30);\nend-ds;\n";
    private static RpgWorkstationLayout Describe(RpgWorkstationRequest request, bool? indicatorArea = true)
    {
        var record = request.Renames.SingleOrDefault()?.Name ?? "ENTRY";
        RpgDisplayRecord[] records = [new("ENTRY", record, [new("VALUE", request.Prefix + "VALUE", RpgFieldKind.Character, 8, 0, true, true)], false, false, null)];
        var signature = indicatorArea is { } flag ? RpgWorkstationLayout.SignatureFor(records, flag) : RpgWorkstationLayout.SignatureFor(records);
        return new("QGPL/SCREEN", signature, records) { IndicatorArea = indicatorArea };
    }
    private static RpgProgram Compile(string body, bool? indicatorArea = true)
        => RpgCompiler.Compile("QGPL", "INDICATORS", "**free\n" + body, workstationResolver: r => Describe(r, indicatorArea));
    private static object? Read(RpgRuntimeContext context, string expression) => RpgExpressionParser.Parse(expression).Eval(context);
    private static byte[] Bytes(RpgRuntimeContext context, string expression = "AREA") => Assert.IsType<ProgramBuffer>(Read(context, expression)).ToArray();
    private sealed class Workstation(Func<RpgWorkstationOperation, IReadOnlyList<bool>, RpgWorkstationResult> transfer) : IRpgWorkstation
    {
        internal int Calls { get; private set; }
        internal int Accepted { get; private set; }
        public RpgWorkstationResult Transfer(RpgWorkstationOperation operation, string record, IReadOnlyDictionary<string, object?> values,
            IReadOnlyList<bool> indicators, int? relativeRecord = null, Action<RpgWorkstationResult>? validate = null)
        {
            Calls++; var result = transfer(operation, indicators); validate?.Invoke(result); Accepted++; return result;
        }
        public void Dispose() { }
    }
    private static RpgWorkstationResult Response(params (int Index, bool Value)[] flags)
        => new(new Dictionary<string, object?>(), flags.ToDictionary(f => f.Index, f => f.Value));

    [Theory]
    [InlineData(37, 0xF0)]
    [InlineData(1208, 0x30)]
    public void INDDS_defaults_all_99_bytes_to_character_zero_and_retains_native_views(int ccsid, byte zero)
    {
        var program = Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" + Area + "return;");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid }).Run(program);
        Assert.Equal(99, Read(context, "%size(AREA)")); Assert.Equal(3, Read(context, "%elem(AREA.FLAGS)"));
        Assert.Equal(false, Read(context, "AREA.EXIT")); Assert.Equal(false, Read(context, "AREA.SHOW"));
        Assert.All(Bytes(context), b => Assert.Equal(zero, b));
    }
    [Fact]
    public void Explicit_subfield_initializers_and_character_overlays_share_the_indicator_bytes()
    {
        var program = Compile("dcl-f SCREEN workstn usropn indds(AREA);\ndcl-ds AREA qualified;\nGROUP char(3) inz('010');\nSECOND ind pos(2);\nON_FLAG ind pos(21) inz(*on);\nend-ds;\nreturn;");
        var context = new RpgInterpreter(new()).Run(program);
        Assert.Equal("010", Read(context, "AREA.GROUP")); Assert.Equal(true, Read(context, "AREA.SECOND")); Assert.Equal(true, Read(context, "AREA.ON_FLAG"));
        Assert.Equal(0xF1, Bytes(context)[20]); Assert.Equal(0xF0, Bytes(context)[98]);
    }
    [Fact]
    public void CLEAR_resets_indicator_subfields_and_the_undeclared_indicator_positions()
    {
        var context = new RpgInterpreter(new()).Run(Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" + Area +
            "eval AREA.SHOW=*on;\neval %subst(AREA:99:1)='1';\nclear AREA;\nreturn;"));
        Assert.All(Bytes(context), b => Assert.Equal(0xF0, b));
    }
    [Fact]
    public void LIKEDS_copies_final_99_byte_layout_without_becoming_an_indicator_bank()
    {
        var context = new RpgInterpreter(new()).Run(Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" + Area +
            "dcl-ds COPY likeds(AREA);\ndcl-ds AGAIN likeds(COPY) inz;\nreturn;"));
        Assert.Equal(99, Read(context, "%size(COPY)")); Assert.Equal(99, Read(context, "%size(AGAIN)"));
        Assert.All(Bytes(context, "COPY"), b => Assert.Equal(0x40, b));
        Assert.All(Bytes(context, "AREA"), b => Assert.Equal(0xF0, b));
    }
    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void File_conditioning_and_response_flags_use_INDDS_without_modifying_global_IN(int ccsid)
    {
        var provider = new Workstation((_, flags) => { Assert.True(flags[21]); Assert.False(flags[0]); return Response((3, true)); });
        var program = Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area + "eval AREA.SHOW=*on;\nexfmt ENTRY;\nreturn;");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, OpenWorkstation = _ => provider }).Run(program);
        Assert.Equal(true, Read(context, "AREA.EXIT")); Assert.Equal(true, Read(context, "AREA.SHOW"));
        Assert.False(context.Indicators[3]); Assert.False(context.Indicators[21]); Assert.Equal(1, provider.Accepted);
    }
    [Fact]
    public void Global_IN_does_not_supply_conditioning_for_a_file_with_INDDS()
    {
        var provider = new Workstation((_, flags) => { Assert.False(flags[21]); return Response(); });
        var context = new RpgInterpreter(new() { OpenWorkstation = _ => provider }).Run(Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area + "seton 21;\nwrite ENTRY;\nreturn;"));
        Assert.True(context.Indicators[21]); Assert.Equal(false, Read(context, "AREA.SHOW"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Separate_files_can_use_independent_or_explicitly_shared_indicator_banks(bool shared)
    {
        var declarations = "dcl-f ONE workstn indds(AREA) rename(ENTRY:FIRST) prefix(A_);\n" +
            "dcl-f TWO workstn indds(" + (shared ? "AREA" : "OTHER") + ") rename(ENTRY:SECOND) prefix(B_);\n" + Area +
            (shared ? "" : "dcl-ds OTHER qualified;\nEXIT ind pos(3);\nend-ds;\n");
        var one = new Workstation((_, _) => Response((3, true)));
        var two = new Workstation((_, flags) => { Assert.Equal(shared, flags[3]); return Response((3, false)); });
        var context = new RpgInterpreter(new() { OpenWorkstation = b => b.Request.Name == "ONE" ? one : two })
            .Run(Compile(declarations + "exfmt FIRST;\nexfmt SECOND;\nreturn;"));
        Assert.Equal(!shared, Read(context, "AREA.EXIT")); Assert.False(context.Indicators[3]);
    }
    [Fact]
    public void Ordinary_files_keep_global_IN_semantics_when_they_have_no_INDDS()
    {
        var provider = new Workstation((_, flags) => { Assert.True(flags[21]); return Response((3, true)); });
        var context = new RpgInterpreter(new() { OpenWorkstation = _ => provider }).Run(Compile("dcl-f SCREEN workstn;\nseton 21;\nexfmt ENTRY;\nreturn;"));
        Assert.True(context.Indicators[3]); Assert.True(context.Indicators[21]);
    }
    [Fact]
    public void Invalid_indicator_bytes_fail_before_I_O_and_are_program_errors_outside_E()
    {
        var provider = new Workstation((_, _) => Response()); var messages = new List<string>();
        var context = new RpgInterpreter(new() { OpenWorkstation = _ => provider, SendMessage = messages.Add }).Run(Compile(
            "dcl-f SCREEN workstn indds(AREA);\n" + Area + "eval %subst(AREA:21:1)='X';\nmonitor;\nwrite(e) ENTRY;\non-error 105;\nsndpgmmsg 'INVALID BANK';\nendmon;\nreturn;"));
        Assert.Equal(0, provider.Calls); Assert.Equal(new[] { "INVALID BANK" }, messages); Assert.Equal(false, Read(context, "%error"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Response_validation_preserves_fields_and_indicator_storage_before_provider_commit(bool local)
    {
        var provider = new Workstation((_, flags) =>
        {
            ((bool[])flags)[21] = true;
            return new(new Dictionary<string, object?> { ["VALUE"] = "AFTER" }, new Dictionary<int, bool> { [3] = true, [0] = true });
        });
        var context = new RpgInterpreter(new() { OpenWorkstation = _ => provider }).Run(Compile("dcl-f SCREEN workstn " +
            (local ? "indds(AREA)" : "") + ";\n" + Area + "eval VALUE='BEFORE';\nread(e) SCREEN;\nreturn;"));
        Assert.Equal("BEFORE  ", context.ReadValue("VALUE")); Assert.False(context.Indicators[21]); Assert.False(context.Indicators[3]);
        Assert.Equal(0, provider.Accepted); Assert.All(Bytes(context), b => Assert.Equal(local ? 0xF0 : 0x40, b));
    }
    [Fact]
    public void Borrowed_native_API_views_update_the_live_bank_and_expire_at_return()
    {
        ProgramArgument? captured = null;
        var provider = new Workstation((_, flags) => { Assert.True(flags[21]); return Response(); });
        var context = new RpgInterpreter(new() { OpenWorkstation = _ => provider, TypedProgramCaller = (_, _, _, borrow) =>
        {
            var arguments = borrow!(); var root = Assert.IsType<ProgramArgument>(arguments[0]); captured = root;
            var show = Assert.IsType<ProgramArgument>(arguments[1]);
            Assert.True(ProgramArgument.Overlaps(root, show)); Assert.Equal(99, Assert.IsType<ProgramBuffer>(root.ToBuffer()).Length); show.Value = true;
            return new RpgExternalCallResult { Success = true };
        } }).Run(Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area + "callp MODIFY(AREA:AREA.SHOW);\nwrite ENTRY;\nreturn;"));
        Assert.Equal(true, Read(context, "AREA.SHOW")); Assert.Throws<ObjectDisposedException>(() => captured!.ToBuffer());
    }
    [Fact]
    public void Retained_RETURN_preserves_conditioning_and_updates_only_response_positions()
    {
        var responses = 0; var provider = new Workstation((_, flags) => { Assert.Equal(responses > 0, flags[3]); responses++; return Response((3, responses == 1)); });
        var host = new RpgHost { RetainWorkstations = true, OpenWorkstation = _ => provider };
        var program = Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area + "exfmt ENTRY;\nreturn;");
        var context = new RpgInterpreter(host).Run(program);
        new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(false, Read(context, "AREA.EXIT")); Assert.Equal(2, responses); context.CloseWorkstations();
    }
    [Theory]
    [InlineData("dcl-s AREA char(99);", "indds(AREA)")]
    [InlineData(Area, "indds(MISSING)")]
    [InlineData(Area, "indds()")]
    [InlineData(Area, "indds('AREA')")]
    [InlineData(Area, "indds(AREA) indds(AREA)")]
    [InlineData("dcl-ds AREA qualified dim(2);\nN ind;\nend-ds;", "indds(AREA)")]
    [InlineData("dcl-ds AREA psds;\nN char(99);\nend-ds;", "indds(AREA)")]
    [InlineData("dcl-ds AREA;\nN int(10);\nend-ds;", "indds(AREA)")]
    [InlineData("dcl-ds AREA len(98);\nN ind;\nend-ds;", "indds(AREA)")]
    [InlineData("dcl-ds AREA len(100);\nN ind;\nend-ds;", "indds(AREA)")]
    [InlineData("dcl-ds AREA;\nN ind pos(100);\nend-ds;", "indds(AREA)")]
    public void Invalid_INDDS_bindings_fail_with_source_locations(string structure, string options)
        => Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("dcl-f SCREEN workstn " + options + ";\n" + structure + "\nreturn;")).Location);
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void INDDS_requires_a_known_DDS_INDARA_description(bool? indicatorArea)
        => Assert.NotNull(Assert.Throws<RpgCompileException>(() => Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area, indicatorArea)).Location);
    [Fact]
    public void INDDS_on_DISK_is_rejected_before_external_resolution()
        => Assert.Throws<RpgCompileException>(() => RpgCompiler.Compile("QGPL", "BAD", "**free\ndcl-f DATA indds(AREA);\n" + Area));
    [Theory]
    [InlineData(null, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void Both_legacy_and_INDARA_aware_saved_bindings_round_trip(bool? indicatorArea, int version)
    {
        var program = Compile("dcl-f SCREEN workstn;\nreturn;", indicatorArea);
        var restored = RpgWorkstationBindings.Restore(program.Source!.Text, RpgWorkstationBindings.Serialize(program));
        Assert.Equal(version, restored.Version); Assert.Equal(indicatorArea, restored.Files.Single().Layout.IndicatorArea);
    }
    [Theory]
    [InlineData("flag")]
    [InlineData("downgrade")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void Saved_indicator_area_metadata_rejects_tampering_and_version_downgrades(string defect)
    {
        var program = Compile("dcl-f SCREEN workstn indds(AREA);\n" + Area); var json = RpgWorkstationBindings.Serialize(program);
        var node = JsonNode.Parse(json)!; var layout = node["files"]![0]!["layout"]!;
        if (defect == "flag") layout["indicatorArea"] = false;
        if (defect == "downgrade") node["version"] = 1;
        if (defect == "missing") { layout.AsObject().Remove("indicatorArea"); layout["signature"] = RpgWorkstationLayout.SignatureFor(program.Workstations.Single().Layout.Records); }
        json = defect == "duplicate" ? json.Replace("\"indicatorArea\":true", "\"indicatorArea\":true,\"indicatorArea\":true") : node.ToJsonString();
        Assert.Throws<InvalidDataException>(() => RpgWorkstationBindings.Restore(program.Source!.Text, json));
    }
    [Fact]
    public void INDDS_LIKEDS_keeps_99_bytes_without_extending_the_ordinary_parent()
    {
        var context = new RpgInterpreter(new()).Run(Compile("dcl-f SCREEN workstn usropn indds(AREA);\ndcl-ds PARENT qualified;\nN ind;\nend-ds;\ndcl-ds AREA likeds(PARENT);\nreturn;"));
        Assert.Equal(99, Read(context, "%size(AREA)")); Assert.Equal(1, Read(context, "%size(PARENT)"));
        Assert.All(Bytes(context), b => Assert.Equal(0xF0, b));
    }
    [Fact]
    public void Final_indicator_layouts_still_enforce_the_structure_array_storage_bound()
        => Assert.Contains("1 MiB", Assert.Throws<RpgCompileException>(() => Compile("dcl-f SCREEN workstn usropn indds(AREA);\n" + Area + "dcl-ds COPY likeds(AREA) dim(20000);\nreturn;")).Message);
    [Fact]
    public void Explicit_LEN_99_and_indicator_subfield_arrays_preserve_native_positions()
    {
        var context = new RpgInterpreter(new()).Run(Compile("dcl-f SCREEN workstn usropn indds(AREA);\ndcl-ds AREA qualified len(99);\nFLAGS ind dim(99);\nend-ds;\neval AREA.FLAGS(99)=*on;\nreturn;"));
        Assert.Equal(99, Read(context, "%size(AREA)")); Assert.Equal(true, Read(context, "AREA.FLAGS(99)")); Assert.Equal(0xF1, Bytes(context)[98]);
    }
}
