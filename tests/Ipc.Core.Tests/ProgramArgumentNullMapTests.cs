using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;

namespace Ipc.Core.Tests;

public sealed class ProgramArgumentNullMapTests
{
    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Host_callbacks_borrow_flags_without_decoding_invalid_null_data_and_expire_on_return(int ccsid)
    {
        ProgramArgumentNullMap? retained = null;
        var program = RpgCompiler.Compile("QGPL", "NULLHOST", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('HOST');\nP packed(5:0) options(*nullind);\nend-pr;\ndcl-ds DATA qualified inz;\nP packed(5:0) nullind;\nRAW char(3) overlay(P);\nend-ds;\neval DATA.RAW='BAD';\neval %nullind(DATA.P)=*on;\ncallp TARGET(DATA.P);\nreturn;");
        var context = new RpgInterpreter(new() { Ccsid = () => ccsid, TypedProgramCaller = (_, _, _, borrow) => {
            var argument = Assert.IsType<ProgramArgument>(borrow!().Single()); retained = Assert.IsType<ProgramArgumentNullMap>(argument.NullMap);
            Assert.True(retained.Read(0)); Assert.Throws<CpfException>(() => retained.Read(1)); retained.Write(0, false); return new() { Success = true };
        } }).Run(program);
        Assert.Equal(false, RpgExpressionParser.Parse("%nullind(DATA.P)").Eval(context)); Assert.Equal("BAD", RpgExpressionParser.Parse("DATA.RAW").Eval(context));
        Assert.Throws<ObjectDisposedException>(() => retained!.Read(0));
    }
    [Fact]
    public void Scoped_storage_identities_expire_and_snapshots_own_independent_state()
    {
        var active = true; var flag = false; var storage = new ProgramArgumentStorage(new object(), 4, 1);
        var original = new ProgramArgumentNullMap(1, _ => true, _ => flag, (_, _) => { }, (_, value) => flag = value, storage: _ => storage);
        var scoped = original.Scoped(() => ObjectDisposedException.ThrowIf(!active, original));
        Assert.Same(storage, scoped.Storage(0)); var detached = scoped.Snapshot(); active = false;
        Assert.Throws<ObjectDisposedException>(() => scoped.Storage(0)); Assert.Null(detached.Storage(0));
        detached.Write(0, true); Assert.True(detached.Read(0)); Assert.False(flag);
    }
    [Fact]
    public void Entry_flags_detach_from_the_callers_lifetime_after_return()
    {
        var active = true; var flag = false; object? data = 7L;
        var map = new ProgramArgumentNullMap(1, _ => true, _ => flag, (_, _) => { }, (_, value) => flag = value).Scoped(() => ObjectDisposedException.ThrowIf(!active, new object()));
        var argument = new ProgramArgument(() => data, value => data = value, value => value, "*INT", 4) { NullMap = map };
        var program = RpgCompiler.Compile("QGPL", "NULLENTRY", "**free\nctl-opt alwnull(*usrctl);\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\neval %nullind(P)=*on;\nreturn;");
        var context = new RpgInterpreter(new()).Run(program, [argument]); active = false;
        Assert.True(flag); Assert.Equal(true, RpgExpressionParser.Parse("%nullind(P)").Eval(context)); Assert.Throws<ObjectDisposedException>(() => map.Read(0));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Readonly_and_nonnullable_map_entries_reject_mutation_before_callbacks(bool readOnly)
    {
        var writes = 0; var map = new ProgramArgumentNullMap(1, _ => readOnly, _ => false, (_, _) => { }, (_, _) => writes++, readOnly);
        Assert.Throws<CpfException>(() => map.Write(0, true)); Assert.Equal(0, writes);
    }
    [Fact]
    public void Missing_entry_map_rejects_before_program_side_effects()
    {
        var sent = false; var program = RpgCompiler.Compile("QGPL", "NULLENTRY", "**free\nctl-opt alwnull(*usrctl);\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\nsndpgmmsg 'BAD';\nreturn;");
        var argument = new ProgramArgument(() => 7L, _ => { }, value => value, "*INT", 4);
        Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { SendMessage = _ => sent = true }).Run(program, [argument])); Assert.False(sent);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Malformed_entry_map_shape_or_capabilities_reject_before_execution(bool wrongCount)
    {
        var sent = false; var map = new ProgramArgumentNullMap(wrongCount ? 2 : 1, _ => wrongCount, _ => false, (_, _) => { }, (_, _) => { });
        var argument = new ProgramArgument(() => 7L, _ => { }, value => value, "*INT", 4) { NullMap = map };
        var program = RpgCompiler.Compile("QGPL", "NULLENTRY", "**free\nctl-opt alwnull(*usrctl);\ndcl-pi *n;\nP int(10) options(*nullind);\nend-pi;\nsndpgmmsg 'BAD';\nreturn;");
        var error = Assert.Throws<RpgRuntimeException>(() => new RpgInterpreter(new() { SendMessage = _ => sent = true }).Run(program, [argument])); Assert.Equal(105, error.StatusCode); Assert.False(sent);
    }
}
