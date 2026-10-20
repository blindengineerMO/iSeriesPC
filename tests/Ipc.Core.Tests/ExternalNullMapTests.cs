using Ipc.Core.Messages;
using Ipc.Core.Work;
using Ipc.Console.Session;

namespace Ipc.Core.Tests;

public sealed partial class ExternalProgramTests
{
    [Theory]
    [InlineData("null-map-only", "3:0:4")] [InlineData("null-update", "12:0:4")] [InlineData("null-failed", "12:0:4")] [InlineData("null-bad-data", "3:1:4")]
    [InlineData("null-missing", "3:1:4")] [InlineData("null-count", "3:1:4")] [InlineData("null-slot", "3:1:4")]
    [InlineData("null-added", "3:1:4")] [InlineData("null-extra", "3:1:4")] [InlineData("null-capability", "3:1:4")]
    [InlineData("null-flag-type", "3:1:4")] [InlineData("null-length", "3:1:4")] [InlineData("null-readonly", "3:1:4")]
    [InlineData("null-duplicate", "3:1:4")]
    public void Protocol_three_stages_all_data_and_null_maps_before_writeback(string mode, string expected)
    {
        Register("NULLTARGET", mode, protocol: 3);
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('NULLTARGET');\nP int(10) options(*nullind);\nSECOND int(10);\nend-pr;\ndcl-s N int(10) nullind inz(3);\ndcl-s SECOND int(10) inz(4);\neval %nullind(N)=*on;\ncallp(e) TARGET(N:SECOND);\nsndpgmmsg %char(N)+':'+%char(%nullind(N))+':'+%char(SECOND);");
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Protocol_three_const_maps_accept_echoes_and_reject_changed_flags(bool changed)
    {
        Register("NULLTARGET", changed ? "null-change" : "null-echo", protocol: 3);
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('NULLTARGET');\nP int(10) const options(*nullind);\nend-pr;\ndcl-s N int(10) nullind inz(3);\ncallp(e) TARGET(N);\nsndpgmmsg %char(N)+':'+%char(%nullind(N))+':'+%char(%error);");
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(changed ? "3:0:1" : "3:0:0", result.Message?.Trim());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Protocol_three_optional_and_omitted_arguments_keep_aligned_map_slots(int count)
    {
        Register("NULLTARGET", "null-echo", protocol: 3);
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('NULLTARGET');\nP int(10) options(*nullind:*nopass:*omit);\nend-pr;\ndcl-s N int(10) nullind inz(3);\ncallp TARGET(" + (count == 0 ? "" : count == 1 ? "*omit" : "N") + ");\nsndpgmmsg %char(N);");
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith("3", result.Message?.Trim());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Protocol_three_rejects_named_null_storage_aliases_before_process_effects(bool nested)
    {
        var path = Path.Combine(_directory, "null-alias-effect.json"); Register("NULLTARGET", "effect", path: path, protocol: 3);
        var procedure = "dcl-proc INNER;\ndcl-pi *n;\nP int(10) options(*nullind);\nFLAG ind;\nend-pi;\ncallp TARGET(P:FLAG);\nend-proc;";
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('NULLTARGET');\nP int(10) options(*nullind);\nFLAG ind;\nend-pr;\ndcl-s FLAG ind;\ndcl-s N int(10) nullind(FLAG);\ncallp " + (nested ? "INNER" : "TARGET") + "(N:FLAG);\nreturn;\n" + (nested ? procedure : ""));
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.True(result.IsError); Assert.Contains("alias-capable", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(37)] [InlineData(1208)]
    public void Protocol_three_buffers_and_null_maps_roundtrip_in_both_ccsids(int ccsid)
    {
        var descriptor = Register("NULLTARGET", "null-update", protocol: 3); var flags = new[] { true, false, true };
        var map = new ProgramArgumentNullMap(3, _ => true, index => flags[index], (_, _) => { }, (index, value) => flags[index] = value);
        var result = _system.ExternalPrograms.Execute(descriptor, [new ProgramBuffer([3, 3, 3], ccsid), 4], nullMaps: [map, null]);
        Assert.True(result.Success); Assert.Equal(new byte[] { 3, 12, 3 }, Assert.IsType<ProgramBuffer>(result.Parameters[0]).ToArray());
        Assert.Equal(ccsid, Assert.IsType<ProgramBuffer>(result.Parameters[0]).Ccsid); Assert.Equal(new[] { false, true, false }, result.NullMaps[0]);
        Assert.Equal(new[] { true, false, true }, flags); // Service returns staged values; the calling host owns publication.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Protocol_three_real_RPG_array_and_nested_maps_keep_declaration_order(bool nested)
    {
        Register("NULLTARGET", "null-update", protocol: 3);
        var declarations = nested ? "dcl-ds MODEL qualified template;\nN int(3) dim(3) nullind;\nTAIL int(3);\nend-ds;\ndcl-ds DATA likeds(MODEL) inz;" : "dcl-s DATA int(3) dim(3) nullind inz(3);";
        var member = nested ? "DATA.N" : "DATA";
        var formal = nested ? "P likeds(MODEL) options(*nullind);" : "P int(3) dim(3) options(*nullind);";
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\n" + declarations + "\ndcl-pr TARGET extpgm('NULLTARGET');\n" + formal + "\nSECOND int(10);\nend-pr;\ndcl-s SECOND int(10) inz(4);\neval %nullind(" + member + "(1))=*on;\ncallp TARGET(DATA:SECOND);\nsndpgmmsg %char(" + member + "(2))+':'+%char(%nullind(" + member + "(1)))+':'+%char(%nullind(" + member + "(2)))+':'+%char(%nullind(" + member + "(3)));");
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith("12:0:1:1", result.Message?.Trim());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Protocol_three_rejects_nonnullable_or_readonly_changes(bool readOnly)
    {
        var descriptor = Register("NULLTARGET", "null-change", protocol: 3);
        var map = new ProgramArgumentNullMap(1, _ => readOnly, _ => false, (_, _) => { }, (_, _) => { }, readOnly);
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(descriptor, [3], nullMaps: [map]));
    }

    [Fact]
    public void Protocol_three_bounds_large_maps_before_starting_a_process()
    {
        var path = Path.Combine(_directory, "null-limit-effect.json"); var descriptor = Register("NULLTARGET", "effect", path: path, protocol: 3);
        var reads = 0; var map = new ProgramArgumentNullMap(10000, _ => true, _ => { reads++; return false; }, (_, _) => { }, (_, _) => { });
        var error = Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(descriptor, [3], nullMaps: [map]));
        Assert.Contains("64 KiB", error.Message); Assert.Equal(0, reads); Assert.False(File.Exists(path));
    }

    [Fact]
    public void Protocol_three_empty_maps_preserve_zero_field_structures()
    {
        var descriptor = Register("NULLTARGET", "null-echo", protocol: 3);
        var map = new ProgramArgumentNullMap(0, _ => throw new Exception(), _ => throw new Exception(), (_, _) => { }, (_, _) => { });
        var result = _system.ExternalPrograms.Execute(descriptor, [new ProgramBuffer([1, 2], 37)], nullMaps: [map]);
        Assert.Empty(result.NullMaps[0]!); Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<ProgramBuffer>(result.Parameters[0]).ToArray());
    }
}
