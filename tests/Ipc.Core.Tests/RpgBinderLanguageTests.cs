using Ipc.Core.Compilation;
using Ipc.Rpg.Model;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class RpgBinderLanguageTests
{
    private static IReadOnlyList<RpgExportLevel> Parse(string text, CancellationToken token = default) => RpgCompiler.ParseBinderSource(new("QGPL/QSRVSRC(API)", text), token);
    private static string Block(string signature, string exports = "FIRST", string level = "*CURRENT") =>
        $"STRPGMEXP PGMLVL({level}) SIGNATURE({signature})\n" + string.Join('\n', exports.Split(' ').Select(e => $"EXPORT SYMBOL({e})")) + "\nENDPGMEXP";

    [Fact]
    public void Literal_signatures_use_sixteen_EBCDIC_bytes_and_symbols_preserve_case()
    {
        var level = Assert.Single(Parse(Block("'ABC'", "'MixedCase'")));
        Assert.Equal("C1C2C3" + string.Concat(Enumerable.Repeat("40", 13)), level.Signature);
        Assert.Equal(["MixedCase"], level.Exports);
    }
    [Fact]
    public void Unquoted_symbols_fold_to_uppercase_while_quoted_symbols_remain_distinct()
    {
        Assert.Equal(["FIRST", "first"], Assert.Single(Parse(Block("*GEN", "first 'first'"))).Exports);
        Assert.Throws<RpgCompileException>(() => Parse(Block("*GEN", "first FIRST")));
    }
    [Fact]
    public void Escaped_quotes_in_signatures_are_encoded_once()
        => Assert.StartsWith("C17DC2", Assert.Single(Parse(Block("'A''B'"))).Signature);
    [Fact]
    public void Hex_signatures_preserve_all_sixteen_bytes()
        => Assert.Equal("0123456789ABCDEF0123456789ABCDEF", Assert.Single(Parse(Block("x'0123456789abcdef0123456789abcdef'"))).Signature);
    [Fact]
    public void Generated_signatures_are_deterministic_and_depend_on_export_order()
    {
        var first = Assert.Single(Parse(Block("*GEN", "FIRST SECOND")));
        Assert.Equal(RpgCompiler.GeneratedExports(["FIRST", "SECOND"]).Signature, first.Signature);
        Assert.NotEqual(RpgCompiler.GeneratedExports(["SECOND", "FIRST"]).Signature, first.Signature);
        Assert.Equal(32, first.Signature.Length);
    }
    [Fact]
    public void Current_and_append_only_previous_export_levels_retain_their_own_signature()
    {
        var levels = Parse(Block("'V2'", "FIRST SECOND") + "\n" + Block("'V1'", "FIRST", "*PRV"));
        Assert.Equal(2, levels.Count); Assert.Equal("*CURRENT", levels[0].Level); Assert.Equal("*PRV", levels[1].Level);
        Assert.Equal(["FIRST"], levels[1].Exports); Assert.NotEqual(levels[0].Signature, levels[1].Signature);
    }
    [Theory]
    [InlineData("+")] [InlineData("-")]
    public void Comments_continuations_case_insensitive_keywords_and_optional_semicolons_are_accepted(string continuation)
    {
        var text = $"/* start\n comment */ strpgmexp {continuation}\n signature('V1') lvlchk(*yes);\nexport /* middle */ symbol('FIRST');\nendpgmexp; /* end */";
        Assert.Equal(["FIRST"], Assert.Single(Parse(text)).Exports);
    }
    [Fact]
    public void Omitted_level_and_signature_default_to_current_and_generated()
        => Assert.Equal(RpgCompiler.GeneratedExports(["FIRST"]).Signature, Assert.Single(Parse("STRPGMEXP\nEXPORT SYMBOL(FIRST)\nENDPGMEXP")).Signature);

    [Theory]
    [InlineData("")]
    [InlineData("/* never closed")]
    [InlineData("STRPGMEXP +")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL(FIRST)")]
    [InlineData("EXPORT SYMBOL(FIRST)")]
    [InlineData("ENDPGMEXP")]
    [InlineData("STRPGMEXP\nENDPGMEXP")]
    [InlineData("STRPGMEXP\nSTRPGMEXP")]
    [InlineData("STRPGMEXP PGMLVL(*PRV)\nEXPORT SYMBOL(FIRST)\nENDPGMEXP")]
    [InlineData("STRPGMEXP PGMLVL(*OTHER)")]
    [InlineData("STRPGMEXP LVLCHK(*NO)")]
    [InlineData("STRPGMEXP LVLCHK")]
    [InlineData("STRPGMEXP SIGNATURE")]
    [InlineData("STRPGMEXP SIGNATURE('V1') SIGNATURE('V2')")]
    [InlineData("STRPGMEXP EXTRA(YES)")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL(FIRST) SYMBOL(SECOND)")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL(FIRST) DATA")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL('FIRST SECOND')")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL(FIRST)\nEXPORT SYMBOL(FIRST)")]
    [InlineData("STRPGMEXP\nEXPORT SYMBOL(FIRST)\nENDPGMEXP EXTRA")]
    [InlineData("STRPGMEXP;;")]
    [InlineData("CALL PGM(QGPL/FIRST)")]
    [InlineData("STRPGMEXP SIGNATURE('bad)\nEXPORT SYMBOL(FIRST)\nENDPGMEXP")]
    public void Invalid_or_unsupported_binder_source_reports_its_source_location(string text)
    {
        var error = Assert.Throws<RpgCompileException>(() => Parse(text));
        Assert.NotNull(error.Location); Assert.Contains("QGPL/QSRVSRC(API)", error.Message);
    }
    [Theory]
    [InlineData("''")] [InlineData("'12345678901234567'")] [InlineData("'😀'")]
    [InlineData("UNQUOTED")] [InlineData("X'00'")] [InlineData("X'GG000000000000000000000000000000'")]
    public void Invalid_signature_encodings_reject(string signature) => Assert.Throws<RpgCompileException>(() => Parse(Block(signature)));
    [Theory]
    [InlineData("FIRST SECOND", "SECOND")] [InlineData("FIRST", "FIRST SECOND")] [InlineData("FIRST SECOND", "SECOND FIRST")]
    public void Previous_levels_cannot_remove_reorder_or_insert_before_existing_exports(string current, string previous)
        => Assert.Throws<RpgCompileException>(() => Parse(Block("'V2'", current) + "\n" + Block("'V1'", previous, "*PRV")));
    [Fact]
    public void Duplicate_signatures_and_repeated_current_levels_reject()
    {
        Assert.Throws<RpgCompileException>(() => Parse(Block("'V1'") + "\n" + Block("'V1'", level: "*PRV")));
        Assert.Throws<RpgCompileException>(() => Parse(Block("'V1'") + "\n" + Block("'V2'")));
    }
    [Fact]
    public void Export_and_level_quotas_are_enforced()
    {
        Assert.Equal(256, Assert.Single(Parse(Block("*GEN", string.Join(' ', Enumerable.Range(1, 256).Select(i => "E" + i))))).Exports.Count);
        Assert.Throws<RpgCompileException>(() => Parse(Block("*GEN", string.Join(' ', Enumerable.Range(1, 257).Select(i => "E" + i)))));
        var levels = Block("'V0'") + string.Concat(Enumerable.Range(1, 15).Select(i => "\n" + Block("'V" + i + "'", level: "*PRV")));
        Assert.Equal(16, Parse(levels).Count);
        Assert.Throws<RpgCompileException>(() => Parse(levels + "\n" + Block("'V16'", level: "*PRV")));
        Assert.Throws<InvalidDataException>(() => RpgCompiler.GeneratedExports([]));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Source_limit_counts_UTF8_bytes(bool multibyte)
        => Assert.Throws<RpgCompileException>(() => Parse(new string(multibyte ? 'é' : 'X', multibyte ? 524289 : 1048577)));
    [Fact]
    public void Cancellation_is_observed_even_with_empty_source()
        => Assert.Throws<OperationCanceledException>(() => Parse("", new CancellationToken(true)));
    [Fact]
    public void Service_binding_needs_no_main_entry_and_checks_internal_import_interfaces()
    {
        var importer = Module("FIRST", "ctl-opt nomain;\ndcl-pr TARGET;\nP int(10);\nend-pr;\ndcl-proc ENTRY export;\ndcl-pi *n;\nP int(10);\nend-pi;\ncallp TARGET(P);\nend-proc;");
        var target = Module("SECOND", "ctl-opt nomain;\ndcl-proc TARGET export;\ndcl-pi *n;\nP int(10);\nend-pi;\neval P=42;\nend-proc;");
        RpgCompiler.BindServiceModules([importer, target]); Assert.All(new[] { importer, target }, p => Assert.False(p.IsBoundProgram));
        Assert.Equal(["ENTRY", "TARGET"], RpgCompiler.ModuleExports([importer, target]).Keys);
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindServiceModules([importer]));
        var wrong = Module("WRONG", "ctl-opt nomain;\ndcl-proc TARGET export;\ndcl-pi *n;\nP char(4);\nend-pi;\nend-proc;");
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindServiceModules([importer, wrong]));
        Assert.Throws<RpgCompileException>(() => RpgCompiler.BindServiceModules([target, target]));
    }
    private static RpgProgram Module(string name, string source) => RpgCompiler.CompileModule("QGPL", name, new SourceDocument("QGPL/SOURCE(" + name + ")", "**free\n" + source));
}
