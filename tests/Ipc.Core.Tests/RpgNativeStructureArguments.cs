using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed partial class RpgNativeStructureTests
{
    [Theory]
    [InlineData(37, "C1C2", "D9C5C1C4E840")]
    [InlineData(1208, "4142", "524541445920")]
    public void Native_API_views_alias_whole_structures_overlays_and_live_typed_subfields(int ccsid, string letters, string ready)
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", """
            **free
            dcl-ds DATA qualified len(16);
              BYTES char(16) ccsid(*hex);
              CODE int(10) pos(1);
              AMOUNT packed(7:2) pos(5);
              NAME char(6) pos(9);
            end-ds;
            callp TEST(DATA:DATA:DATA.CODE:DATA.AMOUNT:DATA.NAME:DATA.BYTES);
            """);
        ProgramArgument[] saved = [];
        var context = new RpgInterpreter(new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, _, parameters, borrow) => {
            Assert.Equal(6, parameters.Count);
            saved = borrow!().Cast<ProgramArgument>().ToArray(); Assert.Same(saved[0], saved[1]);
            Assert.Equal("*CHAR", saved[0].Type); Assert.Equal(16, saved[0].Length);
            Assert.Equal("*INT", saved[2].Type); Assert.Equal(4, saved[2].Length);
            Assert.Equal("*DEC", saved[3].Type); Assert.Equal(7, saved[3].Length); Assert.Equal(2, saved[3].Decimals);
            var blank = ccsid == 37 ? "40" : "20";
            saved[0].Value = new ProgramBuffer(Convert.FromHexString("000000070012345C" + letters + string.Concat(Enumerable.Repeat(blank, 4)) + "FFFE"), ccsid);
            Assert.Equal(7L, saved[2].Value); Assert.Equal(123.45m, saved[3].Value); Assert.Equal("AB    ", saved[4].Value);
            saved[2].Value = -2L; saved[3].Value = -1.25m; saved[4].Value = "READY ";
            Assert.Equal("FFFFFFFE0000125D" + ready + "FFFE", Convert.ToHexString(saved[5].ToBuffer()!.ToArray()));
            return new() { Success = true };
        } }).Run(program);
        Assert.Equal(-2L, Read(context, "DATA.CODE")); Assert.Equal(-1.25m, Read(context, "DATA.AMOUNT")); Assert.Equal("READY ", Read(context, "DATA.NAME"));
        foreach (var argument in saved)
        { Assert.Throws<ObjectDisposedException>(() => argument.ToBuffer()); Assert.Throws<ObjectDisposedException>(() => argument.Value); }
    }
    [Fact]
    public void Borrowing_an_uninitialized_numeric_output_does_not_decode_it_before_the_host_writes_it()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified;\nN packed(5:2);\nend-ds;\ncallp TEST(DATA.N:5);");
        var context = new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, _, parameters, borrow) => {
            Assert.Equal(2, parameters.Count);
            var arguments = borrow!(); var receiver = Assert.IsType<ProgramArgument>(arguments[0]);
            Assert.Equal("404040", Convert.ToHexString(receiver.ToBuffer()!.ToArray()));
            Assert.Equal(5m, arguments[1]); receiver.Value = 12.34m; return new() { Success = true };
        } }).Run(program);
        Assert.Equal(12.34m, Read(context, "DATA.N"));
    }
    [Fact]
    public void Native_argument_preparation_rejects_overflow_bad_decimal_bytes_and_wrong_width_without_writing()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified inz;\nN int(3) inz(7);\nP packed(3:1) inz(1.2);\nend-ds;\ncallp TEST(DATA:DATA.N:DATA.P);");
        new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            var arguments = borrow!().Cast<ProgramArgument>().ToArray();
            var before = arguments[0].ToBuffer()!.ToArray();
            Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => arguments[1].Value = 128m).MessageId);
            Assert.Throws<CpfException>(() => arguments[2].Value = new ProgramBuffer([0xFF, 0xFF], 37));
            Assert.Throws<CpfException>(() => arguments[0].Value = new ProgramBuffer([0], 37));
            Assert.Equal(before, arguments[0].ToBuffer()!.ToArray()); return new() { Success = true };
        } }).Run(program);
    }
    [Fact]
    public void Ordinary_copied_callers_receive_semantic_subfields_and_write_back_qualified_values()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified inz;\nN int(10) inz(7);\nNAME char(4) inz('Joe');\nend-ds;\ncallp TEST(DATA.N:DATA.NAME);");
        var context = new RpgInterpreter(new RpgHost { ProgramCaller = (_, _, parameters) => {
            Assert.Equal(new object?[] { 7L, "Joe " }, parameters); return new() { Success = true, UpdatedParameters = [12L, "Sam"] };
        } }).Run(program);
        Assert.Equal(12L, Read(context, "DATA.N")); Assert.Equal("Sam ", Read(context, "DATA.NAME"));
    }
    [Fact]
    public void Copied_argument_writeback_uses_the_array_element_selected_before_the_call()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-s I int(10) inz(1);\ndcl-ds DATA qualified dim(2) inz;\nN int(5);\nend-ds;\ncallp TEST(I:DATA(I).N);");
        var context = new RpgInterpreter(new RpgHost { ProgramCaller = (_, _, _) => new() { Success = true, UpdatedParameters = [2L, 9L] } }).Run(program);
        Assert.Equal(2L, context.ReadValue("I")); Assert.Equal(9L, Read(context, "DATA(1).N")); Assert.Equal(0L, Read(context, "DATA(2).N"));
    }
    [Fact]
    public void A_lazy_API_borrow_keeps_the_array_element_selected_at_call_entry()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-s I int(10) inz(1);\ndcl-ds DATA qualified dim(2) inz;\nN int(5);\nend-ds;\ncallp TEST(I:DATA(I).N);");
        RpgRuntimeContext context = null!;
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            context.WriteValue("I", 2L); var arguments = borrow!(); Assert.IsType<ProgramArgument>(arguments[1]).Value = 9L; return new() { Success = true };
        } };
        context = new(program, host); new RpgInterpreter(host).Run(program, existingContext: context);
        Assert.Equal(9L, Read(context, "DATA(1).N")); Assert.Equal(0L, Read(context, "DATA(2).N"));
    }
    [Fact]
    public void Native_substrings_count_bytes_and_preserve_the_rest_of_the_structure()
    {
        var context = Run("""
            dcl-ds DATA qualified len(12);
              NAME char(8) inz('éAB');
            end-ds;
            eval %subst(DATA:10:2)=X'FF00';
            eval %subst(DATA.NAME:3:2)='CD';
            """, 1208);
        Assert.Equal("C3A943442020202020FF0020", Hex(context, "DATA"));
        Assert.Equal("C3A9", Hex(context, "%subst(DATA.NAME:1:2)"));
        Assert.Equal(2, Read(context, "%len(%subst(DATA:10:2))")); Assert.Equal(2, Read(context, "%size(%subst(DATA:10:2))"));
    }
    [Fact]
    public void CLEAR_initializes_native_numeric_fields_instead_of_restoring_INZ_values()
    {
        var context = Run("dcl-ds DATA qualified inz;\nN packed(5:2) inz(12.34);\nNAME char(3) inz('Joe');\nend-ds;\nclear DATA;");
        Assert.Equal(0m, Read(context, "DATA.N")); Assert.Equal("   ", Read(context, "DATA.NAME")); Assert.Equal("00000C404040", Hex(context, "DATA"));
    }
    [Fact]
    public void Native_array_arguments_share_contiguous_storage_with_indexed_element_arguments()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified dim(2) inz;\nN int(5);\nend-ds;\ncallp TEST(DATA:DATA(2).N);");
        var context = new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            var arguments = borrow!().Cast<ProgramArgument>().ToArray(); Assert.Equal(4, arguments[0].Length);
            arguments[0].Value = new ProgramBuffer([0, 7, 0, 9], 37); Assert.Equal(9L, arguments[1].Value);
            arguments[1].Value = -2L; Assert.Equal("0007FFFE", Convert.ToHexString(arguments[0].ToBuffer()!.ToArray())); return new() { Success = true };
        } }).Run(program);
        Assert.Equal(7L, Read(context, "DATA(1).N")); Assert.Equal(-2L, Read(context, "DATA(2).N"));
    }
    [Fact]
    public void Native_argument_snapshots_enforce_aggregate_bounds_before_allocating_copies_or_entering_the_host()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA len(32767);\nend-ds;\ncallp TEST(" + string.Join(':', Enumerable.Repeat("DATA", 33)) + ");");
        var called = false; Assert.Throws<CpfException>(() => new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, _, _, _) => {
            called = true; return new() { Success = true };
        } }).Run(program)); Assert.False(called);
    }
    [Fact]
    public void Noncontiguous_array_borrowing_fails_before_an_API_body_can_write()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA;\nROWS char(4) dim(2);\nPART char(2) overlay(ROWS);\nend-ds;\ncallp TEST(PART);");
        var entered = false; Assert.Throws<CpfException>(() => new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            _ = borrow!(); entered = true; return new() { Success = true };
        } }).Run(program)); Assert.False(entered);
    }
    [Fact]
    public void A_compatible_scalar_prototype_can_borrow_a_native_numeric_subfield()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified inz;\nN packed(5:2);\nend-ds;\ncallp TEST(DATA.N);");
        program.Fields.Add(new() { Name = "FORMAL", Kind = Ipc.Rpg.Model.RpgFieldKind.Packed, Length = 5, Decimals = 2 });
        program.Prototypes.Add(new() { Name = "TEST", ExternalName = "FAKE", Parameters = { "FORMAL" } });
        var context = new RpgInterpreter(new RpgHost { TypedProgramCaller = (_, name, _, borrow) => {
            Assert.Equal("FAKE", name); Assert.IsType<ProgramArgument>(Assert.Single(borrow!())).Value = 12.34m; return new() { Success = true };
        } }).Run(program);
        Assert.Equal(12.34m, Read(context, "DATA.N"));
    }
    [Fact]
    public void Retained_native_bytes_keep_their_CCSID_and_require_explicit_conversion_for_a_changed_API_CCSID()
    {
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds DATA qualified inz;\nNAME char(3) inz('Joe');\nend-ds;\ncallp TEST(DATA);");
        var context = new RpgRuntimeContext(program, new RpgHost { Ccsid = () => 37 }); var entered = false;
        Assert.Throws<CpfException>(() => new RpgInterpreter(new RpgHost { Ccsid = () => 1208, TypedProgramCaller = (_, _, _, borrow) => {
            _ = borrow!(); entered = true; return new() { Success = true };
        } }).Run(program, existingContext: context));
        Assert.False(entered); Assert.Equal("Joe", Read(context, "DATA.NAME")); Assert.Equal("D19685", Hex(context, "DATA"));
    }
}
