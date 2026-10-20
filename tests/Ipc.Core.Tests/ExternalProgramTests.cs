using System.Diagnostics;
using System.Text.Json;
using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Core.Work;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Ipc.Session.Transport;

namespace Ipc.Core.Tests;

public sealed partial class ExternalProgramTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ipc-external-test-" + Guid.NewGuid().ToString("N"));
    private readonly IpcSystem _system;
    private static string Runtime => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Environment.ProcessPath!;
    private string Fixture => Path.Combine(_directory, "fixture", "Ipc.NativeFixture.dll");
    public ExternalProgramTests()
    {
        _system = IpcSystem.Create(_directory); _system.Start();
        Directory.CreateDirectory(Path.GetDirectoryName(Fixture)!);
        foreach (var extension in new[] { ".dll", ".runtimeconfig.json", ".deps.json" })
        {
            var target = Path.ChangeExtension(Fixture, extension);
            File.Copy(Path.ChangeExtension(typeof(NativeFixture.Program).Assembly.Location, extension), target);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private ObjectDescriptor Register(string name, string mode = "echo", int timeout = 60, string? path = null, int protocol = 1)
    {
        _system.ExternalPrograms.Register("QGPL", name, Runtime, new[] { Fixture, mode }.Concat(path is null ? Array.Empty<string>() : new[] { path }),
            new[] { Path.ChangeExtension(Fixture, ".runtimeconfig.json"), Path.ChangeExtension(Fixture, ".deps.json") }, timeout, protocol);
        return _system.Objects.GetRequired("QGPL", name, ObjectType.Program);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void RPG_null_maps_reject_unsupported_process_protocols_before_side_effects(int protocol)
    {
        var path = Path.Combine(_directory, "null-map-effect.json"); Register("NULLTARGET", "effect", path: path, protocol: protocol);
        ClExternalCallTests.Create(_system, "NULLCALL", "RPG", "**free\nctl-opt alwnull(*usrctl);\ndcl-pr TARGET extpgm('NULLTARGET');\nP int(10) options(*nullind);\nend-pr;\ndcl-s N int(10) nullind;\ncallp TARGET(N);");
        var result = new CommandService(_system).Execute("CALL QGPL/NULLCALL"); Assert.True(result.IsError); Assert.Contains("null-map-aware", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RPG_native_and_unprototyped_aliases_reject_before_external_process_side_effects(bool prototype)
    {
        var path = Path.Combine(_directory, "rpg-alias-effect.json"); Register("SIGTARGET", "effect", path: path);
        var source = "**free\n" + (prototype ? "dcl-pr RUN extpgm('SIGTARGET');\nA int(3);\nB int(3);\nend-pr;\n" : "") +
            "dcl-s N int(3) inz(7);\ncallp " + (prototype ? "RUN" : "SIGTARGET") + "(N:N);";
        ClExternalCallTests.Create(_system, "SIGCALLER", "RPG", source);
        var result = new CommandService(_system).Execute("CALL QGPL/SIGCALLER");
        Assert.True(result.IsError); Assert.Contains("alias-capable", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false, "rpg-update", "12:4")] [InlineData(true, "rpg-update", "12:4")]
    [InlineData(false, "rpg-update-fail", "12:4")] [InlineData(true, "rpg-update-fail", "12:4")]
    [InlineData(false, "rpg-update-bad", "3:4")] [InlineData(true, "rpg-update-bad", "3:4")]
    public void RPG_external_responses_validate_every_reference_before_writeback_and_keep_valid_error_updates(bool prototype, string mode, string expected)
    {
        Register("SIGTARGET", mode, protocol: 2);
        var source = "**free\n" + (prototype ? "dcl-pr RUN extpgm('SIGTARGET');\nA int(10);\nB int(10);\nend-pr;\n" : "") +
            "dcl-s N int(10) inz(3);\ndcl-s OTHER int(10) inz(4);\ncallp(e) " + (prototype ? "RUN" : "SIGTARGET") + "(N:OTHER);\nsndpgmmsg %char(N)+':'+%char(OTHER);";
        ClExternalCallTests.Create(_system, "SIGCALLER", "RPG", source);
        var result = new CommandService(_system).Execute("CALL QGPL/SIGCALLER");
        Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public void CONST_external_echoes_keep_readonly_metadata_through_direct_and_nested_CL_calls(bool throughCl, bool changed)
    {
        Register("CONSTEXT", (throughCl ? "cl-const-" : "rpg-const-") + (changed ? "change" : "echo"), protocol: 2);
        if (throughCl) ClExternalCallTests.Create(_system, "CONSTCL", "CLP", "PGM PARM(&N &INPUT)\nDCL &N *INT LEN(4)\nDCL &INPUT *INT LEN(4)\nCALL CONSTEXT PARM(&N &INPUT)\nENDPGM");
        var source = "**free\ndcl-pr RUN extpgm('" + (throughCl ? "CONSTCL" : "CONSTEXT") + "');\nOUTPUT int(10);\nINPUT int(10) const;\nend-pr;\ndcl-s N int(10) inz(3);\n" +
            "callp(e) RUN(N:4);\nsndpgmmsg %char(N);";
        ClExternalCallTests.Create(_system, "CONSTPGM", "RPG", source);
        var result = new CommandService(_system).Execute("CALL QGPL/CONSTPGM"); Assert.False(result.IsError, result.Message);
        Assert.EndsWith(changed ? "3" : "12", result.Message?.Trim());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void Native_optional_prototypes_preserve_actual_counts_in_the_external_process_protocol(int count)
    {
        var path = Path.Combine(_directory, "optional-arguments.json"); Register("OPTEXT", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "OPTCALLER", "RPG", "**free\ndcl-pr RUN extpgm('OPTEXT');\nN int(10) options(*nopass);\nTEXT char(4) const options(*nopass);\nend-pr;\n" +
            "dcl-s N int(10) inz(7);\ncallp RUN(" + (count == 0 ? "" : count == 1 ? "N" : "N:'ABCD'") + ");\nsndpgmmsg %char(N);");
        var result = new CommandService(_system).Execute("CALL QGPL/OPTCALLER"); Assert.False(result.IsError, result.Message); Assert.EndsWith("7", result.Message?.Trim());
        using var document = JsonDocument.Parse(File.ReadAllText(path)); Assert.Equal(count, document.RootElement.GetArrayLength());
        if (count > 0) Assert.Equal(7, document.RootElement[0].GetInt32());
        if (count == 2) Assert.Equal("ABCD", document.RootElement[1].GetString());
    }

    [Theory]
    [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)]
    public void Native_OMIT_process_arguments_use_null_slots_and_validate_every_return_before_writeback(int protocol, bool changed)
    {
        Register("OMITEXT", changed ? "rpg-omit-change" : "rpg-omit-echo", protocol: protocol);
        ClExternalCallTests.Create(_system, "OMITCALL", "RPG", "**free\ndcl-pr RUN extpgm('OMITEXT');\nN int(10);\nA int(3) options(*omit);\nB char(4) const options(*omit);\nend-pr;\n" +
            "dcl-s N int(10) inz(7);\ncallp(e) RUN(N:*omit:*omit);\nsndpgmmsg %char(N);");
        var result = new CommandService(_system).Execute("CALL QGPL/OMITCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(changed ? "7" : "12", result.Message?.Trim());
    }

    [Theory]
    [InlineData("rpg-update", "12")] [InlineData("rpg-update-fail", "12")] [InlineData("rpg-update-bad", "3")]
    public void Local_native_functions_borrow_private_receivers_for_real_external_process_calls(string mode, string expected)
    {
        Register("PROCTARGET", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "PROCCALL", "RPG", "**free\ndcl-pr EXT extpgm('PROCTARGET');\nA int(10);\nB int(10);\nend-pr;\ndcl-s RESULT int(10);\neval RESULT=GET();\nsndpgmmsg %char(RESULT);\nreturn;\n" +
            "dcl-proc GET;\ndcl-pi *n int(10) end-pi;\ndcl-s A int(10) inz(3);\ndcl-s B int(10) inz(4);\ncallp(e) EXT(A:B);\nreturn A;\nend-proc;");
        var result = new CommandService(_system).Execute("CALL QGPL/PROCCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Fact]
    public void Defined_command_passes_a_native_CPP_typed_scalars_and_binary_list_and_file_layouts()
    {
        var path = Path.Combine(_directory, "command-arguments.json"); Register("CMDCPP", "effect", path: path);
        var source = "CMD\nPARM KWD(LIST) TYPE(*CHAR) LEN(3) MAX(3)\nPARM KWD(FILE) TYPE(QFILE) FILE(*IN)\nPARM KWD(COUNT) TYPE(*INT4)\nQFILE: QUAL TYPE(*NAME) LEN(10)\nQUAL TYPE(*NAME) LEN(10) DFT(*LIBL)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/CMDCPP");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "NATIVECMD", source, definition, "QSECOFR");
        var result = new CommandService(_system).Execute("NATIVECMD LIST(A B) FILE(QGPL/ORDERS) COUNT(12)"); Assert.False(result.IsError, result.Message);
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var arguments = document.RootElement;
        Assert.Equal("0002C14040C24040", Convert.ToHexString(Ipc.Core.Text.CodePage.ToBytes(37, arguments[0].GetString()!)));
        Assert.Equal("ORDERS    QGPL      ", arguments[1].GetString()); Assert.Equal(12, arguments[2].GetInt32());
    }

    [Theory]
    [InlineData("rpg-aggregate-update", "12:3")] [InlineData("rpg-aggregate-bad", "3:3")]
    public void Aggregate_process_buffers_validate_later_outputs_before_committing_a_prefix(string mode, string expected)
    {
        Register("AGGEXT", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "AGGCALL", "RPG", "**free\ndcl-pr RUN extpgm('AGGEXT');\nARR int(3) dim(3);\nN int(10);\nend-pr;\ndcl-s ARR int(3) dim(4) inz(3);\ndcl-s N int(10) inz(4);\ncallp(e) RUN(ARR:N);\nsndpgmmsg %char(ARR(2))+':'+%char(ARR(4));");
        var result = new CommandService(_system).Execute("CALL QGPL/AGGCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Fact]
    public void Aggregate_and_element_process_aliases_reject_before_starting_the_process()
    {
        var path = Path.Combine(_directory, "aggregate-effect.json"); Register("AGGEXT", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "AGGCALL", "RPG", "**free\ndcl-pr RUN extpgm('AGGEXT');\nARR int(3) dim(3);\nN int(3);\nend-pr;\ndcl-s ARR int(3) dim(4) inz(3);\ncallp RUN(ARR:ARR(2));");
        var result = new CommandService(_system).Execute("CALL QGPL/AGGCALL"); Assert.True(result.IsError); Assert.Contains("alias-capable", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("rpg-aggregate-update", "12")] [InlineData("rpg-aggregate-bad", "3")]
    public void Native_array_return_images_capture_validated_external_updates_before_the_local_frame_exits(string mode, string expected)
    {
        Register("RETEXT", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "RETCALL", "RPG", "**free\ndcl-pr RUN extpgm('RETEXT');\nA int(3) dim(3);\nN int(10);\nend-pr;\ndcl-s RESULT int(3) dim(3);\neval RESULT=GET();\nsndpgmmsg %char(RESULT(2));\nreturn;\ndcl-proc GET;\ndcl-pi *n int(3) dim(3) rtnparm end-pi;\ndcl-s LOCAL int(3) dim(3) inz(3);\ndcl-s N int(10) inz(4);\ncallp(e) RUN(LOCAL:N);\nreturn LOCAL;\nend-proc;");
        var result = new CommandService(_system).Execute("CALL QGPL/RETCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Theory]
    [InlineData("rpg-aggregate-update", "12")] [InlineData("rpg-aggregate-bad", "3")]
    public void Variable_array_process_calls_transfer_actual_storage_and_validate_all_outputs_before_commit(string mode, string expected)
    {
        Register("VAREXT", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "VARCALL", "RPG", "**free\ndcl-pr RUN extpgm('VAREXT');\nA int(3) dim(100) options(*varsize);\nN int(10);\nend-pr;\ndcl-s A int(3) dim(3) inz(3);\ndcl-s N int(10) inz(4);\ncallp(e) RUN(A:N);\nsndpgmmsg %char(A(2));");
        var result = new CommandService(_system).Execute("CALL QGPL/VARCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Fact]
    public void Variable_array_and_element_process_aliases_reject_before_process_side_effects()
    {
        var path = Path.Combine(_directory, "variable-effect.json"); Register("VAREXT", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "VARCALL", "RPG", "**free\ndcl-pr RUN extpgm('VAREXT');\nA int(3) dim(100) options(*varsize);\nN int(3);\nend-pr;\ndcl-s A int(3) dim(3) inz(3);\ncallp RUN(A:A(2));");
        var result = new CommandService(_system).Execute("CALL QGPL/VARCALL"); Assert.True(result.IsError); Assert.Contains("alias-capable", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Transformed_CONST_process_echoes_validate_all_outputs_before_committing(bool changed)
    {
        Register("OPTEXT", changed ? "rpg-options-change" : "rpg-options-echo", protocol: 2);
        ClExternalCallTests.Create(_system, "OPTCALL", "RPG", "**free\ndcl-pr RUN extpgm('OPTEXT');\nN int(10);\nP char(4) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s N int(10) inz(3);\ndcl-s A char(8) inz('AB');\ncallp(e) RUN(N:A);\nsndpgmmsg %char(N)+':'+%trim(A);");
        var result = new CommandService(_system).Execute("CALL QGPL/OPTCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(changed ? "3:AB" : "12:AB", result.Message?.Trim());
    }

    [Fact]
    public void Exact_character_rejection_prevents_process_side_effects()
    {
        var path = Path.Combine(_directory, "exact-effect.json"); Register("OPTEXT", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "OPTCALL", "RPG", "**free\ndcl-pr RUN extpgm('OPTEXT');\nP char(4) const options(*exact);\nend-pr;\nmonitor;\ncallp(e) RUN('ABCDE');\non-error 105;\nsndpgmmsg 'EXACT REJECTED';\nendmon;");
        var result = new CommandService(_system).Execute("CALL QGPL/OPTCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith("EXACT REJECTED", result.Message?.Trim()); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LIKE_character_roots_reach_processes_as_transformed_readonly_values(bool changed)
    {
        Register("LIKEXT", changed ? "rpg-options-change" : "rpg-options-echo", protocol: 2);
        ClExternalCallTests.Create(_system, "LIKECALL", "RPG", "**free\ndcl-s ROOT char(4);\ndcl-pr RUN extpgm('LIKEXT');\nN int(10);\nP like(ROOT) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s N int(10) inz(3);\ndcl-s A like(ROOT:+4) inz('AB');\ncallp(e) RUN(N:A);\nsndpgmmsg %char(N)+':'+%trim(A);");
        var result = new CommandService(_system).Execute("CALL QGPL/LIKECALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(changed ? "3:AB" : "12:AB", result.Message?.Trim());
    }

    [Fact]
    public void LIKE_exact_rejects_untyped_values_before_process_launch()
    {
        var path = Path.Combine(_directory, "like-effect.json"); Register("LIKEXT", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "LIKECALL", "RPG", "**free\ndcl-s ROOT char(4);\ndcl-pr RUN extpgm('LIKEXT');\nP like(ROOT) const options(*exact);\nend-pr;\nmonitor;\ncallp RUN('AB');\non-error 105;\nsndpgmmsg 'LIKE EXACT REJECTED';\nendmon;");
        var result = new CommandService(_system).Execute("CALL QGPL/LIKECALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith("LIKE EXACT REJECTED", result.Message?.Trim()); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Forward_LIKE_prototypes_keep_readonly_process_response_atomicity(bool changed)
    {
        Register("FWDEXT", changed ? "rpg-options-change" : "rpg-options-echo", protocol: 2);
        ClExternalCallTests.Create(_system, "FWDCALL", "RPG", "**free\ndcl-pr RUN extpgm('FWDEXT');\nN int(10);\nP like(ROOT) const options(*trim:*rightadj:*exact);\nend-pr;\ndcl-s A like(ROOT:+4) inz('AB');\ndcl-s ROOT char(4);\ndcl-s N int(10) inz(3);\ncallp(e) RUN(N:A);\nsndpgmmsg %char(N)+':'+%trim(A);");
        var result = new CommandService(_system).Execute("CALL QGPL/FWDCALL"); Assert.False(result.IsError, result.Message); Assert.EndsWith(changed ? "3:AB" : "12:AB", result.Message?.Trim());
    }

    [Fact]
    public void Unrelated_forward_exact_types_reject_creation_before_process_side_effects()
    {
        var path = Path.Combine(_directory, "forward-effect.json"); Register("FWDEXT", "effect", path: path, protocol: 2);
        var source = "**free\ndcl-pr RUN extpgm('FWDEXT');\nP like(ROOT) const options(*exact);\nend-pr;\ndcl-s A char(4);\ndcl-s ROOT char(4);\ncallp RUN(A);";
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects); files.CreateSourceFile("QGPL", "FWDSRC", sourceWidth: 240); files.SaveSourceMember("QGPL", "FWDSRC", "FWDCALL", source, null);
        var result = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/FWDCALL) SRCFILE(QGPL/FWDSRC)"); Assert.True(result.IsError); Assert.Contains("*EXACT", result.Message); Assert.False(File.Exists(path)); Assert.False(_system.Objects.Exists("QGPL", "FWDCALL", ObjectType.Program));
    }

    [Theory]
    [InlineData("rpg-aggregate-update", "12")] [InlineData("rpg-aggregate-bad", "3")]
    public void Forward_structure_process_outputs_validate_completely_before_writeback(string mode, string expected)
    {
        Register("FDSEXT", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "FDSCALL", "RPG", "**free\ndcl-pr RUN extpgm('FDSEXT');\nP likeds(ROOT);\nN int(10);\nend-pr;\ndcl-ds A likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified inz;\nC int(3) dim(3) inz(3);\nend-ds;\ndcl-s N int(10) inz(4);\ncallp(e) RUN(A:N);\nsndpgmmsg %char(A.C(2));");
        var result = new CommandService(_system).Execute("CALL QGPL/FDSCALL"); Assert.False(result.IsError,result.Message); Assert.EndsWith(expected,result.Message?.Trim());
    }

    [Fact]
    public void Cyclic_forward_structure_templates_prevent_process_side_effects()
    {
        var path = Path.Combine(_directory,"fds-effect.json"); Register("FDSEXT","effect",path:path,protocol:2);
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects); files.CreateSourceFile("QGPL","FDSSRC",sourceWidth:240);
        files.SaveSourceMember("QGPL","FDSSRC","FDSCALL","**free\ndcl-ds A likeds(B) inz(*likeds);\ndcl-ds B likeds(A) inz(*likeds);\ncallp FDSEXT(A);",null);
        var result = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/FDSCALL) SRCFILE(QGPL/FDSSRC)"); Assert.True(result.IsError); Assert.Contains("Cyclic",result.Message); Assert.False(File.Exists(path)); Assert.False(_system.Objects.Exists("QGPL","FDSCALL",ObjectType.Program));
    }

    [Theory]
    [InlineData("rpg-aggregate-update", "12")] [InlineData("rpg-aggregate-bad", "3")]
    public void Data_template_process_buffers_preserve_atomic_response_validation(string mode, string expected)
    {
        Register("TMPLEXT", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "TMPLCALL", "RPG", "**free\ndcl-pr RUN extpgm('TMPLEXT');\nP likeds(ROOT);\nN int(10);\nend-pr;\ndcl-ds A likeds(ROOT) inz(*likeds);\ndcl-ds ROOT qualified template inz;\nC int(3) dim(3) inz(3);\nend-ds;\ndcl-s N int(10) inz(4);\ncallp(e) RUN(A:N);\nsndpgmmsg %char(A.C(2));");
        var result = new CommandService(_system).Execute("CALL QGPL/TMPLCALL"); Assert.False(result.IsError,result.Message); Assert.EndsWith(expected,result.Message?.Trim());
    }

    [Fact]
    public void Template_data_operands_reject_creation_before_process_side_effects()
    {
        var path = Path.Combine(_directory,"template-effect.json"); Register("TMPLEXT","effect",path:path,protocol:2);
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects); files.CreateSourceFile("QGPL","TMPLSRC",sourceWidth:240);
        files.SaveSourceMember("QGPL","TMPLSRC","TMPLCALL","**free\ndcl-s ROOT char(4) template;\ncallp TMPLEXT(ROOT);",null);
        var result = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/TMPLCALL) SRCFILE(QGPL/TMPLSRC)"); Assert.True(result.IsError); Assert.Contains("TEMPLATE",result.Message); Assert.False(File.Exists(path)); Assert.False(_system.Objects.Exists("QGPL","TMPLCALL",ObjectType.Program));
    }

    private void CreateRecordProcessFile(string name="RECFILE", string record="REC")
    {
        var format = new Ipc.Db.Definitions.RecordFormat { Name=record, Fields=[
            new() { Name="A",Type=Ipc.Db.Definitions.FieldType.Binary,Length=1 },
            new() { Name="B",Type=Ipc.Db.Definitions.FieldType.Binary,Length=1 },
            new() { Name="C",Type=Ipc.Db.Definitions.FieldType.Binary,Length=1 }] };
        format.AssignPositions();
        new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects).CreatePhysicalFile("QGPL",name,
            new() { Name=name,Attribute=Ipc.Db.Definitions.FileAttribute.Physical,Formats=[format] },"");
    }

    [Theory]
    [InlineData("rpg-aggregate-update","12")] [InlineData("rpg-aggregate-bad","3")]
    public void LIKEREC_process_buffers_validate_all_outputs_before_writeback(string mode,string expected)
    {
        CreateRecordProcessFile(); Register("RECEXT",mode,protocol:2);
        var source="**free\ndcl-f RECFILE;\ndcl-pr RUN extpgm('RECEXT');\nP likerec(REC);\nN int(10);\nend-pr;\ndcl-ds BUFFER likerec(REC) inz;\ndcl-s N int(10) inz(4);\neval BUFFER.A=3;\neval BUFFER.B=3;\neval BUFFER.C=3;\ncallp(e) RUN(BUFFER:N);\nsndpgmmsg %char(BUFFER.B);";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects); files.CreateSourceFile("QGPL","RECSRC",sourceWidth:240); files.SaveSourceMember("QGPL","RECSRC","RECCALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/RECCALL) SRCFILE(QGPL/RECSRC)"); Assert.False(created.IsError,created.Message); files.RemoveMember("QGPL","RECSRC","RECCALL");
        var result=new CommandService(_system).Execute("CALL QGPL/RECCALL"); Assert.False(result.IsError,result.Message); Assert.EndsWith(expected,result.Message?.Trim());
    }

    [Fact]
    public void Unrelated_LIKEREC_exact_arguments_prevent_process_launch()
    {
        CreateRecordProcessFile(); CreateRecordProcessFile("OTHERFILE","OTHERREC");
        var path=Path.Combine(_directory,"record-effect.json"); Register("RECEXT","effect",path:path,protocol:2);
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects); files.CreateSourceFile("QGPL","RECSRC",sourceWidth:240);
        files.SaveSourceMember("QGPL","RECSRC","RECCALL","**free\ndcl-f RECFILE;\ndcl-f OTHERFILE;\ndcl-pr RUN extpgm('RECEXT');\nP likerec(REC) options(*exact);\nend-pr;\ndcl-ds BUFFER likerec(OTHERREC) inz;\ncallp RUN(BUFFER);",null);
        var result=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/RECCALL) SRCFILE(QGPL/RECSRC)"); Assert.True(result.IsError); Assert.Contains("*EXACT",result.Message); Assert.False(File.Exists(path)); Assert.False(_system.Objects.Exists("QGPL","RECCALL",ObjectType.Program));
    }

    [Theory]
    [InlineData("rpg-aggregate-update","12")] [InlineData("rpg-aggregate-bad","3")]
    public void LIKEFILE_families_preserve_process_response_atomicity(string mode,string expected)
    {
        CreateRecordProcessFile();Register("FILEEXT",mode,protocol:2);
        var source="**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/RECFILE');\ndcl-f COPY likefile(MODEL) extfile('QGPL/RECFILE');\ndcl-pr RUN extpgm('FILEEXT');\nP likerec(MODEL.REC) options(*exact);\nN int(10);\nend-pr;\ndcl-ds BUFFER likerec(COPY.REC) inz;\ndcl-s N int(10) inz(4);\neval BUFFER.A=3;\neval BUFFER.B=3;\neval BUFFER.C=3;\ncallp(e) RUN(BUFFER:N);\nsndpgmmsg %char(BUFFER.B);";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","FILESRC",sourceWidth:240);files.SaveSourceMember("QGPL","FILESRC","FILECALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/FILECALL) SRCFILE(QGPL/FILESRC)");Assert.False(created.IsError,created.Message);files.RemoveMember("QGPL","FILESRC","FILECALL");
        var result=new CommandService(_system).Execute("CALL QGPL/FILECALL");Assert.False(result.IsError,result.Message);Assert.EndsWith(expected,result.Message?.Trim());
    }

    [Fact]
    public void File_template_arguments_prevent_process_launch_and_program_creation()
    {
        CreateRecordProcessFile();var path=Path.Combine(_directory,"file-template-effect.json");Register("FILEEXT","effect",path:path,protocol:2);
        var source="**free\ndcl-f MODEL template extdesc('QGPL/RECFILE');\ncallp FILEEXT(MODEL);";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","FILESRC",sourceWidth:240);files.SaveSourceMember("QGPL","FILESRC","FILECALL",source,null);
        var result=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/FILECALL) SRCFILE(QGPL/FILESRC)");Assert.True(result.IsError);Assert.Contains("TEMPLATE",result.Message);Assert.False(File.Exists(path));Assert.False(_system.Objects.Exists("QGPL","FILECALL",ObjectType.Program));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void LIKEFILE_file_arguments_reject_before_external_process_side_effects(int protocol)
    {
        CreateRecordProcessFile();var path=Path.Combine(_directory,"file-parameter-effect.json");Register("FILEEXT","effect",path:path,protocol:protocol);
        var source="**free\ndcl-f MODEL template block(*no) extdesc('QGPL/RECFILE');\ndcl-f COPY likefile(MODEL) extfile('QGPL/RECFILE');\ndcl-pr RUN extpgm('FILEEXT');\nF likefile(MODEL);\nend-pr;\ncallp RUN(COPY);";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","FILESRC",sourceWidth:240);files.SaveSourceMember("QGPL","FILESRC","FILECALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/FILECALL) SRCFILE(QGPL/FILESRC)");Assert.False(created.IsError,created.Message);
        var result=new CommandService(_system).Execute("CALL QGPL/FILECALL");Assert.True(result.IsError);Assert.Contains("only between RPG",result.Message);Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("rpg-aggregate-update","12")] [InlineData("rpg-aggregate-bad","3")]
    public void Procedure_local_record_buffers_preserve_process_writeback_atomicity(string mode,string expected)
    {
        CreateRecordProcessFile();Register("LOCALCPP",mode,protocol:2);
        var source="**free\ndcl-f MODEL qualified template block(*no) extdesc('QGPL/RECFILE');\ndcl-pr RUN extpgm('LOCALCPP');\nP likerec(MODEL.REC) options(*exact);\nN int(10);\nend-pr;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f LOCAL likefile(MODEL) extfile('QGPL/RECFILE');\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(LOCAL.REC) inz;\ndcl-s N int(10) inz(4);\neval BUFFER.A=3;\neval BUFFER.B=3;\neval BUFFER.C=3;\ncallp(e) RUN(BUFFER:N);\nsndpgmmsg %char(BUFFER.B);\nend-proc;";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","LOCALSRC",sourceWidth:240);files.SaveSourceMember("QGPL","LOCALSRC","LOCALCALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/LOCALCALL) SRCFILE(QGPL/LOCALSRC)");Assert.False(created.IsError,created.Message);files.RemoveMember("QGPL","LOCALSRC","LOCALCALL");
        var result=new CommandService(_system).Execute("CALL QGPL/LOCALCALL");Assert.False(result.IsError,result.Message);Assert.EndsWith(expected,result.Message?.Trim());
    }

    [Fact]
    public void Procedure_local_file_arguments_reject_before_external_process_side_effects()
    {
        CreateRecordProcessFile();var path=Path.Combine(_directory,"local-file-effect.json");Register("LOCALCPP","effect",path:path,protocol:2);
        var source="**free\ndcl-f MODEL template block(*no) extdesc('QGPL/RECFILE');\ndcl-pr RUN extpgm('LOCALCPP');\nF likefile(MODEL);\nend-pr;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f LOCAL likefile(MODEL) extfile('QGPL/RECFILE');\ndcl-pi *n end-pi;\ncallp RUN(LOCAL);\nend-proc;";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","LOCALSRC",sourceWidth:240);files.SaveSourceMember("QGPL","LOCALSRC","LOCALCALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/LOCALCALL) SRCFILE(QGPL/LOCALSRC)");Assert.False(created.IsError,created.Message);
        var result=new CommandService(_system).Execute("CALL QGPL/LOCALCALL");Assert.True(result.IsError);Assert.Contains("only between RPG",result.Message);Assert.False(File.Exists(path));
    }

    private sealed class WorkstationBufferTerminal : Ipc.Session.IProgramTerminal
    {
        public Ipc.Dsp.PanelResponse Read(Ipc.Session.ProgramDisplay display,CancellationToken cancellationToken)
            => throw new InvalidOperationException("The buffer process fixture performs no terminal input.");
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void Workstation_file_parameters_reject_before_external_process_side_effects(int protocol)
    {
        var path=Path.Combine(_directory,"workstation-file-effect.json");Register("WSFILEEXT","effect",path:path,protocol:protocol);
        var dds=DisplayPanelTests.Dds()+DisplayPanelTests.Dds("REC",record:true)+DisplayPanelTests.Dds("TEXT",4,usage:'B',row:2,column:2);
        new Ipc.Dsp.DisplayFileStore(_system.Connections).Create("QGPL","SCREEN",dds);
        var source="**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f COPY likefile(MODEL) usropn;\ndcl-pr RUN extpgm('WSFILEEXT');\nF likefile(MODEL);\nend-pr;\ncallp RUN(COPY);";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);files.CreateSourceFile("QGPL","WSSRC",sourceWidth:240);files.SaveSourceMember("QGPL","WSSRC","WSFILE",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/WSFILE) SRCFILE(QGPL/WSSRC)");Assert.False(created.IsError,created.Message);
        var result=new CommandService(_system).Execute("CALL QGPL/WSFILE");Assert.True(result.IsError);Assert.Contains("only between RPG",result.Message);Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(37,false,false)] [InlineData(1208,false,false)] [InlineData(37,true,false)] [InlineData(1208,true,false)]
    [InlineData(37,false,true)] [InlineData(1208,false,true)] [InlineData(37,true,true)] [InlineData(1208,true,true)]
    public void Workstation_record_process_buffers_use_zoned_layout_and_atomic_writeback(int ccsid,bool malformed,bool copied)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"),"WorkstationBuffer9!");
        Register("WSBUFFER",malformed?"rpg-workstation-bad":"rpg-workstation-update",protocol:2);
        var dds=DisplayPanelTests.Dds()+DisplayPanelTests.Dds("REC",record:true)+DisplayPanelTests.Dds("TEXT",4,usage:'B',row:2,column:2)+DisplayPanelTests.Dds("NUMBER",5,type:'Y',decimals:2,usage:'B',row:3,column:2);
        new Ipc.Dsp.DisplayFileStore(_system.Connections).Create("QGPL","SCREEN",dds);
        var definition=copied?"dcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-f SCREEN likefile(MODEL) extfile(*extdesc);":"dcl-f SCREEN workstn extdesc('QGPL/SCREEN') extfile(*extdesc);";
        var record=copied?"SCREEN.REC":"REC";
        var source="**free\n"+definition+"\ndcl-pr RUN extpgm('WSBUFFER');\nP likerec("+record+":*all) options(*exact);\nN int(10);\nend-pr;\ndcl-ds BUFFER likerec(REC:*all) inz;\ndcl-s N int(10) inz(4);\neval BUFFER.TEXT='KEEP';\neval BUFFER.NUMBER=17.25;\ncallp(e) RUN(BUFFER:N);\nif BUFFER.TEXT='"+(malformed?"KEEP":"NEXT")+"' and BUFFER.NUMBER="+(malformed?"17.25":"20.50")+";\nsndpgmmsg 'WORKSTN PROCESS OK';\nendif;\nseton LR;";
        var files=new Ipc.Db.Store.SqliteFileStore(_system.Connections,_system.Objects);
        files.CreateSourceFile("QGPL","WSSRC",sourceWidth:240);files.SaveSourceMember("QGPL","WSSRC","WSCALL",source,null);
        var created=new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/WSCALL) SRCFILE(QGPL/WSSRC)");Assert.False(created.IsError,created.Message);
        files.RemoveMember("QGPL","WSSRC","WSCALL");
        using var session=new Ipc.Session.ExecutionSession(_system,_system.Jobs.CreateInteractive("QSECOFR",ccsid:ccsid),CancellationToken.None,new WorkstationBufferTerminal());
        var result=session.Execute("CALL QGPL/WSCALL");Assert.False(result.IsError,result.Message);Assert.Contains("WORKSTN PROCESS OK",result.Message);
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Procedure_local_workstation_buffers_preserve_process_writeback_atomicity(int ccsid, bool malformed)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "LocalWorkstation9!");
        Register("WSBUFFER", malformed ? "rpg-workstation-bad" : "rpg-workstation-update", protocol: 2);
        var dds = DisplayPanelTests.Dds() + DisplayPanelTests.Dds("REC", record: true) + DisplayPanelTests.Dds("TEXT", 4, usage: 'B', row: 2, column: 2) + DisplayPanelTests.Dds("NUMBER", 5, type: 'Y', decimals: 2, usage: 'B', row: 3, column: 2);
        new Ipc.Dsp.DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", dds);
        var source = "**free\ndcl-f MODEL workstn qualified template extdesc('QGPL/SCREEN');\ndcl-pr RUN extpgm('WSBUFFER');\nP likerec(MODEL.REC:*all) options(*exact);\nN int(10);\nend-pr;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f LOCAL likefile(MODEL) extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BUFFER likerec(LOCAL.REC:*all) inz;\ndcl-s N int(10) inz(4);\neval BUFFER.TEXT='KEEP';\neval BUFFER.NUMBER=17.25;\ncallp(e) RUN(BUFFER:N);\nif BUFFER.TEXT='" + (malformed ? "KEEP" : "NEXT") + "' and BUFFER.NUMBER=" + (malformed ? "17.25" : "20.50") + ";\nsndpgmmsg 'LOCAL WORKSTN PROCESS OK';\nendif;\nend-proc;";
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects);
        files.CreateSourceFile("QGPL", "WSSRC", sourceWidth: 240); files.SaveSourceMember("QGPL", "WSSRC", "WSCALL", source, null);
        var created = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/WSCALL) SRCFILE(QGPL/WSSRC)"); Assert.False(created.IsError, created.Message);
        files.RemoveMember("QGPL", "WSSRC", "WSCALL");
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None, new WorkstationBufferTerminal());
        var result = session.Execute("CALL QGPL/WSCALL"); Assert.False(result.IsError, result.Message); Assert.Contains("LOCAL WORKSTN PROCESS OK", result.Message);
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void Nested_workstation_buffers_preserve_process_writeback_atomicity(int ccsid, bool malformed)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "LocalWorkstation9!");
        Register("WSBUFFER", malformed ? "rpg-workstation-bad" : "rpg-workstation-update", protocol: 2);
        var dds = DisplayPanelTests.Dds() + DisplayPanelTests.Dds("REC", record: true) + DisplayPanelTests.Dds("TEXT", 4, usage: 'B', row: 2, column: 2) + DisplayPanelTests.Dds("NUMBER", 5, type: 'Y', decimals: 2, usage: 'B', row: 3, column: 2);
        new Ipc.Dsp.DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", dds);
        var source = "**free\ndcl-f MODEL workstn qualified template extdesc('QGPL/SCREEN');\ndcl-pr RUN extpgm('WSBUFFER');\nP likerec(MODEL.REC:*all) options(*exact);\nN int(10);\nend-pr;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f LOCAL likefile(MODEL) extfile(*extdesc);\ndcl-pi *n end-pi;\ndcl-ds BOX qualified inz;\nHEAD char(2);\nBUFFER likerec(LOCAL.REC:*all);\nTAIL char(2);\nend-ds;\ndcl-s N int(10) inz(4);\neval BOX.HEAD='AA';\neval BOX.TAIL='ZZ';\neval BOX.BUFFER.TEXT='KEEP';\neval BOX.BUFFER.NUMBER=17.25;\ncallp(e) RUN(BOX.BUFFER:N);\nif BOX.BUFFER.TEXT='" + (malformed ? "KEEP" : "NEXT") + "' and BOX.BUFFER.NUMBER=" + (malformed ? "17.25" : "20.50") + " and BOX.HEAD='AA' and BOX.TAIL='ZZ';\nsndpgmmsg 'NESTED WORKSTN PROCESS OK';\nendif;\nend-proc;";
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects);
        files.CreateSourceFile("QGPL", "WSSRC", sourceWidth: 240); files.SaveSourceMember("QGPL", "WSSRC", "WSCALL", source, null);
        var created = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/WSCALL) SRCFILE(QGPL/WSSRC)"); Assert.False(created.IsError, created.Message);
        files.RemoveMember("QGPL", "WSSRC", "WSCALL");
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None, new WorkstationBufferTerminal());
        var result = session.Execute("CALL QGPL/WSCALL"); Assert.False(result.IsError, result.Message); Assert.Contains("NESTED WORKSTN PROCESS OK", result.Message);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public void Procedure_local_workstation_arguments_reject_before_external_process_side_effects(int protocol)
    {
        var path = Path.Combine(_directory, "local-workstation-effect.json"); Register("WSFILEEXT", "effect", path: path, protocol: protocol);
        var dds = DisplayPanelTests.Dds() + DisplayPanelTests.Dds("REC", record: true) + DisplayPanelTests.Dds("TEXT", 4, usage: 'B', row: 2, column: 2);
        new Ipc.Dsp.DisplayFileStore(_system.Connections).Create("QGPL", "SCREEN", dds);
        var source = "**free\ndcl-f MODEL workstn template extdesc('QGPL/SCREEN');\ndcl-pr RUN extpgm('WSFILEEXT');\nF likefile(MODEL);\nend-pr;\ncallp WORK();\nseton LR;\ndcl-proc WORK;\ndcl-f LOCAL likefile(MODEL) usropn;\ndcl-pi *n end-pi;\ncallp RUN(LOCAL);\nend-proc;";
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects);
        files.CreateSourceFile("QGPL", "WSSRC", sourceWidth: 240); files.SaveSourceMember("QGPL", "WSSRC", "WSFILE", source, null);
        var created = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/WSFILE) SRCFILE(QGPL/WSSRC)"); Assert.False(created.IsError, created.Message);
        var result = new CommandService(_system).Execute("CALL QGPL/WSFILE"); Assert.True(result.IsError); Assert.Contains("only between RPG", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(37, false)] [InlineData(1208, false)] [InlineData(37, true)] [InlineData(1208, true)]
    public void EXTNAME_process_buffers_survive_description_removal_and_preserve_atomic_writeback(int ccsid, bool malformed)
    {
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ExternalStructure9!");
        CreateRecordProcessFile(); Register("EXTDSCPP", malformed ? "rpg-aggregate-bad" : "rpg-aggregate-update", protocol: 2);
        var source = "**free\ndcl-ds MODEL extname('QGPL/RECFILE') qualified template;\nend-ds;\ndcl-pr RUN extpgm('EXTDSCPP');\nP likeds(MODEL) options(*exact);\nN int(10);\nend-pr;\ndcl-ds BOX qualified inz;\nHEAD char(2);\nBUFFER likeds(MODEL);\nTAIL char(2);\nend-ds;\ndcl-s N int(10) inz(4);\neval BOX.HEAD='AA';\neval BOX.TAIL='ZZ';\neval BOX.BUFFER.A=3;\neval BOX.BUFFER.B=3;\neval BOX.BUFFER.C=3;\ncallp(e) RUN(BOX.BUFFER:N);\nif BOX.BUFFER.B=" + (malformed ? "3" : "12") + " and BOX.HEAD='AA' and BOX.TAIL='ZZ';\nsndpgmmsg 'EXTNAME PROCESS OK';\nendif;\nseton LR;";
        var files = new Ipc.Db.Store.SqliteFileStore(_system.Connections, _system.Objects); files.CreateSourceFile("QGPL", "EXTSRC", sourceWidth: 240); files.SaveSourceMember("QGPL", "EXTSRC", "EXTCALL", source, null);
        var created = new CommandService(_system).Execute("CRTBNDRPG PGM(QGPL/EXTCALL) SRCFILE(QGPL/EXTSRC)"); Assert.False(created.IsError, created.Message);
        files.RemoveMember("QGPL", "EXTSRC", "EXTCALL"); files.DeleteFile("QGPL", "RECFILE");
        using var session = new Ipc.Session.ExecutionSession(_system, _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid), CancellationToken.None, new WorkstationBufferTerminal());
        var result = session.Execute("CALL QGPL/EXTCALL"); Assert.False(result.IsError, result.Message); Assert.Contains("EXTNAME PROCESS OK", result.Message);
    }

    [Fact]
    public void Defined_command_uses_version_two_binary_lists_in_a_UTF8_job()
    {
        var path = Path.Combine(_directory, "utf8-command.json"); Register("UTF8CPP", "effect", path: path, protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "BufferFixture9!");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: 1208);
        var source = "CMD\nPARM KWD(NUMBERS) TYPE(*INT2) MAX(3)\nPARM KWD(TEXT) TYPE(*CHAR) LEN(8)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/UTF8CPP");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "UTF8CMD", source, definition, "QSECOFR");
        var result = new CommandService(_system, job).Execute("UTF8CMD (-2 255) TEXT('é')"); Assert.False(result.IsError, result.Message);
        using var document = JsonDocument.Parse(File.ReadAllText(path)); var arguments = document.RootElement;
        Assert.Equal("buffer", arguments[0].GetProperty("type").GetString()); Assert.Equal(1208, arguments[0].GetProperty("ccsid").GetInt32());
        Assert.Equal("0002FFFE00FF", Convert.ToHexString(arguments[0].GetProperty("data").GetBytesFromBase64()));
        Assert.Equal("é", arguments[1].GetString());
    }

    [Fact]
    public void Version_two_native_buffers_preserve_all_bytes_under_UTF8_and_return_them_without_text_conversion()
    {
        var program = Register("BUFFERS", protocol: 2);
        var input = new Ipc.Core.Work.ProgramBuffer(Enumerable.Range(0, 256).Select(n => (byte)n).ToArray(), 1208);
        var result = _system.ExternalPrograms.Execute(program, new object?[] { input, 12m, "plain" });
        Assert.True(result.Success, result.Message);
        var output = Assert.IsType<Ipc.Core.Work.ProgramBuffer>(result.Parameters[0]);
        Assert.Equal(1208, output.Ccsid); Assert.Equal(input.ToArray(), output.ToArray());
        Assert.Equal(12m, result.Parameters[1]); Assert.Equal("plain", result.Parameters[2]);
        var copy = output.ToArray(); copy[0] = 99; Assert.Equal(0, output.ToArray()[0]);
        Assert.Throws<CpfException>(() => input.ToText());
        var legacy = Register("LEGACY");
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(legacy, new object?[] { input }));
    }

    [Fact]
    public void UTF8_command_list_bytes_pass_through_CL_native_call_and_named_data_area()
    {
        Register("RAWECHO", protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "RawCharacterFixture9");
        ClExternalCallTests.Create(_system, "RAWCL", "CLP", "PGM PARM(&RAW)\nDCL &RAW *CHAR LEN(6)\nCALL QGPL/RAWECHO PARM(&RAW)\nCRTDTAARA DTAARA(QGPL/BYTES) TYPE(*CHAR) LEN(6) VALUE(&RAW)\nRETURN");
        var source = "CMD\nPARM KWD(NUMBERS) TYPE(*INT2) MAX(3)";
        var definition = new Ipc.Cl.Definitions.CommandDefinitionCompiler().Compile(source, "QGPL/RAWCL");
        new Ipc.Services.Commands.CommandDefinitionStore(_system.Connections).Create("QGPL", "RAWCMD", source, definition, "QSECOFR");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: 1208);
        var result = new CommandService(_system, job).Execute("RAWCMD NUMBERS(-2 255)"); Assert.False(result.IsError, result.Message);
        var data = new DataAreaStore(_system.Connections).Read("QGPL", "BYTES");
        Assert.Equal("0002FFFE00FF", Convert.ToHexString(Assert.IsType<ProgramBuffer>(data.Value).ToArray()));
    }

    [Fact]
    public void Native_process_receives_typed_parameters_and_CL_preserves_spaces_and_quotes()
    {
        var descriptor = Register("ECHO");
        var result = _system.ExternalPrograms.Execute(descriptor, new object?[] { "-c 'danger' with spaces", 12.25m, true, null });
        Assert.True(result.Success);
        Assert.Equal(new object?[] { "-c 'danger' with spaces", 12.25m, true, null }, result.Parameters);
        _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "WRAPPER"), ObjectType = ObjectType.Program, Attribute = "CLP",
            Source = "PGM\nCALL PGM(QGPL/ECHO) PARM('two words' 'it''s intact')\nENDPGM" });
        var call = new CommandService(_system).Execute("CALL PGM(QGPL/WRAPPER)");
        Assert.False(call.IsError, call.Message);
        Assert.Contains("two words", call.Message);
        Assert.Contains("intact", call.Message);
        // Direct CALL and the CL fallback must both use the same scalar pipe protocol.
        call = new CommandService(_system).Execute("CALL PGM(QGPL/ECHO) PARM('two words' 'it''s intact')");
        Assert.False(call.IsError, call.Message);
        Assert.Contains("two words", call.Message);
        Assert.Contains("intact", call.Message);
    }

    [Theory]
    [InlineData(37)]
    [InlineData(1208)]
    public void Native_protocol_two_CALL_constants_have_identical_direct_and_compiled_wire_bytes(int ccsid)
    {
        var path = Path.Combine(_directory, "call-constants.json"); Register("CONSTABI", "effect", path: path, protocol: 2);
        _system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QSECOFR"), "ConstantFixture22");
        var job = _system.Jobs.CreateInteractive("QSECOFR", ccsid: ccsid);
        const string command = "CALL QGPL/CONSTABI PARM('ABC' 25.5 X'FF00' (-2.99 (*INT 4)) (1.5 (*FLT 8)))";
        ClExternalCallTests.Create(_system, "CONSTWRAP", "CLP", command);
        var service = new CommandService(_system, job); byte[][]? previous = null;
        foreach (var call in new[] { command, "CALL QGPL/CONSTWRAP" })
        {
            var result = service.Execute(call); Assert.False(result.IsError, result.Message);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var buffers = document.RootElement.EnumerateArray().Select(value => { Assert.Equal("buffer", value.GetProperty("type").GetString()); Assert.Equal(ccsid, value.GetProperty("ccsid").GetInt32()); return value.GetProperty("data").GetBytesFromBase64(); }).ToArray();
            Assert.Equal(Ipc.Core.Text.CodePage.ToBytes(ccsid, "ABC" + new string(' ', 29)), buffers[0]);
            Assert.Equal("000000002550000C", Convert.ToHexString(buffers[1])); Assert.Equal("FF00", Convert.ToHexString(buffers[2]));
            Assert.Equal("FFFFFFFE", Convert.ToHexString(buffers[3])); Assert.Equal("3FF8000000000000", Convert.ToHexString(buffers[4]));
            if (previous is not null) for (var index = 0; index < buffers.Length; index++) Assert.Equal(previous[index], buffers[index]);
            previous = buffers;
        }
    }

    [Fact]
    public void Invalid_native_temporary_response_prevents_all_reference_writeback()
    {
        Register("BADCONST", "cl-update-bad", protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "DCL &N *INT VALUE(3)\nCALL QGPL/BADCONST PARM(&N (4 (*INT 4)))\nMONMSG IPC0006\nSNDPGMMSG MSG(&N)");
        var result = new CommandService(_system).Execute("CALL QGPL/CALLER");
        Assert.False(result.IsError, result.Message); Assert.Equal("3", result.Message?.Trim());
    }

    [Theory]
    [InlineData("cl-update", "12:4")]
    [InlineData("cl-update-fail", "12:4")]
    [InlineData("cl-update-bad", "3:4")]
    public void CL_native_buffer_writeback_is_validated_before_assignment_and_survives_reported_failure(string mode, string expected)
    {
        Register("UPDATE", mode, protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "PGM\nDCL &N *INT VALUE(3)\nDCL &OTHER *INT VALUE(4)\nCALL PGM(QGPL/UPDATE) PARM(&N &OTHER)\nMONMSG (CPF9898 IPC0006)\nSNDPGMMSG MSG(&N *CAT ':' *CAT &OTHER)\nENDPGM");
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.False(result.IsError, result.Message); Assert.EndsWith(expected, result.Message?.Trim());
    }

    [Fact]
    public void Native_aliases_are_rejected_before_process_side_effects()
    {
        var path = Path.Combine(_directory, "aliased-effect.json"); Register("ALIAS", "effect", path: path, protocol: 2);
        ClExternalCallTests.Create(_system, "CALLER", "CLP", "PGM\nDCL &N *INT VALUE(3)\nCALL PGM(QGPL/ALIAS) PARM(&N &N)\nENDPGM");
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/CALLER)");
        Assert.True(result.IsError); Assert.Contains("aliased", result.Message); Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("malformed")]
    [InlineData("flood")]
    public void Native_failures_are_command_errors(string mode)
    {
        // This case verifies exit/protocol failures, not startup timing under parallel load.
        // The separate timeout case retains its one-second deadline.
        Register("FAILURE", mode, timeout: 15);
        var result = new CommandService(_system).Execute("CALL PGM(QGPL/FAILURE)");
        Assert.True(result.IsError, result.Message);
        if (mode == "fail") Assert.Contains("code 7", result.Message);
    }

    [Fact]
    public async Task Timeout_and_cancellation_terminate_the_actual_process()
    {
        var pid = Path.Combine(_directory, "pid");
        var program = Register("TIMEOUT", "sleep", 1, pid);
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>()));
        var processId = int.Parse(File.ReadAllText(pid));
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
        File.Delete(pid);
        program = Register("CANCEL", "sleep", 60, pid);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>(), cancellation.Token));
        while (!File.Exists(pid)) await Task.Delay(20, cancellation.Token);
        processId = int.Parse(File.ReadAllText(pid));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
    }

    [Fact]
    public void Changed_dependencies_and_unprivileged_native_registration_are_rejected()
    {
        var dependency = Path.Combine(_directory, "dependency"); File.WriteAllText(dependency, "one");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(dependency, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        _system.ExternalPrograms.Register("QGPL", "PINNED", Runtime, new[] { Fixture, "echo" }, new[] { dependency });
        File.WriteAllText(dependency, "two");
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(_system.Objects.GetRequired("QGPL", "PINNED", ObjectType.Program), Array.Empty<object?>()));
        _system.Security.Profiles.Create(new UserProfile { Name = "NATIVEUSER", SpecialAuthorities = SpecialAuthority.AllObject });
        using var identity = OperationIdentity.Enter("NATIVEUSER");
        Assert.Throws<CpfException>(() => Register("DENIED"));
        Assert.Throws<CpfException>(() => _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "FORGED"),
            ObjectType = ObjectType.Program, Attribute = ExternalProgramService.Attribute, Owner = "NATIVEUSER", Source = "{}" }));
    }

    [Theory]
    [InlineData("NATIVE")]
    [InlineData("RPG")]
    [InlineData("CLROUTE")]
    [InlineData("UNMATCHED")]
    public async Task SBMJOB_executes_programs_and_routes_with_durable_effect_and_completion_log(string mode)
    {
        var effect = Path.Combine(_directory, "native-effect.json"); Register("NATIVE", "effect", path: effect);
        if (mode == "RPG") _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "RPGPGM"), ObjectType = ObjectType.Program,
            Attribute = "RPG", Source = "**free\ncallp NATIVE('batch value');" });
        if (mode == "CLROUTE")
        {
            _system.Objects.Create(new ObjectDescriptor { Key = new("QGPL", "ROUTEPGM"), ObjectType = ObjectType.Program, Attribute = "CLP",
                Source = "PGM PARM(&CMD)\nDCL VAR(&CMD) TYPE(*CHAR) LEN(1024)\nSNDPGMMSG MSG(&CMD)\nCALL PGM(QGPL/NATIVE) PARM('batch value')\nENDPGM" });
            new RoutingTable(_system.Connections).EnsureEntry("QBATCH", 1, "QCMDB", "QGPL/ROUTEPGM");
        }
        if (mode == "UNMATCHED") new RoutingTable(_system.Connections).RemoveEntry("QBATCH", 9999);
        _system.Security.Profiles.Create(new UserProfile { Name = "TESTER", PasswordHash = Password("PASS1234") });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token);
        await server.Ready.WaitAsync(stop.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", stop.Token);
            var command = mode == "RPG" ? "CALL PGM(QGPL/RPGPGM)" : "CALL PGM(QGPL/NATIVE) PARM('batch value')";
            var reply = await api.ExecuteAsync($"SBMJOB CMD({command}) JOB(NATIVEJOB)", stop.Token);
            Assert.True(reply.Success, reply.Result?.Message);
            Job job;
            do { await Task.Delay(25, stop.Token); job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "NATIVEJOB"); }
            while (job.Status != JobStatus.Completed);
            if (mode == "UNMATCHED")
            {
                Assert.Equal(JobCompletion.Abnormal, job.CompletionCode);
                Assert.Contains("No matching", job.CompletionMessage);
                Assert.False(File.Exists(effect));
                Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
                return;
            }
            Assert.Equal(JobCompletion.Normal, job.CompletionCode);
            if (mode == "CLROUTE") Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.Text?.Contains(command) == true);
            Assert.Equal("batch value", JsonDocument.Parse(File.ReadAllText(effect)).RootElement[0].GetString());
            Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.Text?.Contains("native:") == true);
            Assert.Contains(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private static string Password(string value) => Ipc.Services.Security.PasswordHasher.Hash(value);

    [Fact]
    public async Task Native_job_accounting_records_process_cpu_threads_and_exit()
    {
        var release = Path.Combine(_directory, "cpu-release"); Register("CPU", "cpu", path: release);
        var job = _system.Jobs.CreateInteractive("QUSER");
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var execution = new Ipc.Session.ExecutionSession(_system, job, stop.Token);
        var call = Task.Factory.StartNew(() => execution.Execute("CALL PGM(QGPL/CPU)"), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var info = new JobInformationStore(_system.Connections);
        try
        {
            while (!info.Processes(job.Key).Any(p => p.CpuNanoseconds > 0 && p.PeakThreads > 0)) await Task.Delay(20, stop.Token);
            File.WriteAllText(release, "release");
            var result = await call.WaitAsync(stop.Token); Assert.False(result.IsError, result.Message);
            var process = Assert.Single(info.Processes(job.Key));
            Assert.Equal("Exited", process.State); Assert.Equal(0, process.ExitCode); Assert.True(process.CpuNanoseconds > 0);
            Assert.True(process.PeakThreads > 0); Assert.Equal(0, info.Accounting(job.Key).ActiveProcesses);
            Assert.Equal(process.CpuNanoseconds, info.Accounting(job.Key).NativeCpuNanoseconds);
        }
        finally
        {
            File.WriteAllText(release, "release"); stop.Cancel();
            try { await call.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* Drain the owned process worker on failure. */ }
        }
    }

    [Fact]
    public async Task Native_timeout_terminates_descendant_processes()
    {
        var pid = Path.Combine(_directory, "tree-pid"); var program = Register("TREE", "tree", 2, pid);
        Assert.Throws<CpfException>(() => _system.ExternalPrograms.Execute(program, Array.Empty<object?>()));
        var childPid = int.Parse(File.ReadAllText(pid + ".child"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            try { using var child = Process.GetProcessById(childPid); if (child.HasExited) break; }
            catch (ArgumentException) { break; }
            await Task.Delay(20, timeout.Token);
        }
    }

    [Fact]
    public async Task Server_shutdown_kills_native_batch_process_and_persists_cancelled_outcome()
    {
        var pidFile = Path.Combine(_directory, "native-pid"); Register("SLEEP", "sleep", 60, pidFile);
        _system.Security.Profiles.Create(new UserProfile { Name = "TESTER", PasswordHash = Password("PASS1234") });
        using var stop = new CancellationTokenSource(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new SessionServer(_directory); var run = server.RunAsync(stop.Token); await server.Ready.WaitAsync(deadline.Token);
        try
        {
            await using var api = await CommandConnection.ConnectAsync(server.SocketPath, "TESTER", "PASS1234", deadline.Token);
            Assert.True((await api.ExecuteAsync("SBMJOB CMD(CALL PGM(QGPL/SLEEP)) JOB(STOPNATIVE)", deadline.Token)).Success);
            while (!File.Exists(pidFile)) await Task.Delay(20, deadline.Token);
            var processId = int.Parse(File.ReadAllText(pidFile));
            stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10));
            var job = Assert.Single(_system.Jobs.List(), j => j.Key.Name == "STOPNATIVE");
            Assert.Equal(JobExecutionState.Cancelled, job.ExecutionState);
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(processId));
            Assert.Single(_system.Jobs.GetLog(job.Key), e => e.MessageType == "COMPLETION");
        }
        finally { stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    public void Dispose() { _system.Dispose(); Directory.Delete(_directory, true); }
}
