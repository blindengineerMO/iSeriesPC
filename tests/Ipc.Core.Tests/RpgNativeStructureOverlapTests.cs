using Ipc.Api;
using Ipc.Core.Messages;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Rpg.Parsing;
using Ipc.Rpg.Runtime;
using Ipc.Services;
using Ipc.Services.Messages;

namespace Ipc.Core.Tests;

public sealed partial class RpgNativeStructureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Invalid_UTF8_character_output_is_rejected_before_dequeue_while_opaque_output_preserves_bytes(bool opaque, bool array)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var store = new DataQueueStore(system.Connections); store.Create(new("QGPL", "APIQUEUE"), 8);
        store.Send(new("QGPL", "APIQUEUE"), new ProgramBuffer([0xFF, 0, 0xFE], 1208));
        var declaration = opaque ? "ccsid(*hex) inz(X'" + (array ? "5441494C" : "5441494C5441494C") + "')" : "inz('" + (array ? "TAIL" : "TAILTAIL") + "')";
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds STATE qualified len(11);\nLENGTH packed(5:0) inz(8);\nDATA char(" + (array ? "4) dim(2) " : "8) ") +
            declaration + ";\nend-ds;\ncallp QRCVDTAQ('APIQUEUE':'QGPL':STATE.LENGTH:STATE.DATA:0);");
        var host = new RpgHost { Ccsid = () => 1208, TypedProgramCaller = (_, target, _, borrow) => {
            new DataQueueApi(system.Connections, _ => ["QGPL"], 1208).Invoke(target, borrow!()); return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host); var before = Hex(context, "STATE");
        if (opaque)
        {
            new RpgInterpreter(host).Run(program, existingContext: context);
            Assert.Equal("FF00FE4C5441494C", Hex(context, "STATE.DATA")); Assert.Equal(3m, Read(context, "STATE.LENGTH"));
            Assert.Equal(0, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
        else
        {
            Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program, existingContext: context));
            Assert.Equal(before, Hex(context, "STATE")); Assert.Equal(1, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
    }
    [Theory]
    [InlineData("QWCRSVAL")]
    [InlineData("QWCRDTAA")]
    [InlineData("QUSRTVUS")]
    [InlineData("QMHRTVM")]
    public void Partial_error_receiver_aliases_are_rejected_before_error_reporting_can_overwrite_other_storage(string api)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds STATE qualified len(128);\nPROVIDED int(10) inz(16);\nERROR char(16) ccsid(*hex) pos(1);\nend-ds;\ncallp TEST(STATE:STATE.ERROR);");
        var host = new RpgHost { TypedProgramCaller = (_, _, _, borrow) => {
            var args = borrow!(); var receiver = args[0]; var error = args[1];
            switch (api)
            {
                case "QWCRSVAL": new SystemValueApi(system.Connections).Retrieve([receiver, 128, 1, "QCCSID    ", error]); break;
                case "QWCRDTAA": new DataAreaApi(system.Connections, _ => ["QGPL"]).Retrieve([receiver, 128, "AREA      QGPL      ", -1, 1, error]); break;
                case "QUSRTVUS": new UserSpaceApi(system.Connections, _ => ["QGPL"]).Invoke(api, ["SPACE     QGPL      ", 1, 128, receiver, error]); break;
                case "QMHRTVM": new MessageApi(system.Connections, _ => ["QGPL"]).Invoke(api, [receiver, 128, "RTVM0100", "CPF9898", "QCPFMSG   QSYS      ", "", 0, "*YES", "*NO", error]); break;
            }
            return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host); var before = Hex(context, "STATE");
        Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program, existingContext: context)).MessageId);
        Assert.Equal(before, Hex(context, "STATE"));
    }
    [Theory]
    [InlineData(37, 1, true)]
    [InlineData(37, 4, false)]
    [InlineData(1208, 1, true)]
    [InlineData(1208, 4, false)]
    public void Native_output_ranges_reject_partial_aliases_before_dequeue_and_allow_adjacent_receivers(int ccsid, int position, bool overlap)
    {
        using var system = IpcSystem.Create(":memory:"); system.Start();
        var store = new DataQueueStore(system.Connections); store.Create(new("QGPL", "APIQUEUE"), 8);
        store.Send(new("QGPL", "APIQUEUE"), new(CodePage.ToBytes(ccsid, "READY"), ccsid));
        var program = RpgCompiler.Compile("QGPL", "DSAPI", "**free\ndcl-ds STATE qualified len(11);\nLENGTH packed(5:0) inz(8) pos(1);\n" +
            "DATA char(8) ccsid(*hex) pos(" + position + ");\nend-ds;\ncallp QRCVDTAQ('APIQUEUE':'QGPL':STATE.LENGTH:STATE.DATA:0);");
        var host = new RpgHost { Ccsid = () => ccsid, TypedProgramCaller = (_, target, _, borrow) => {
            var args = borrow!();
            Assert.Equal(overlap, ProgramArgument.Overlaps(args[2], args[3]));
            var storage = Assert.IsType<ProgramArgument>(args[3]).Storage!; Assert.IsNotType<byte[]>(storage.Owner);
            new DataQueueApi(system.Connections, _ => ["QGPL", "QSYS"], ccsid).Invoke(target, args); return new() { Success = true };
        } };
        var context = new RpgRuntimeContext(program, host); var before = Hex(context, "STATE");
        if (overlap)
        {
            Assert.Equal("CPF3C3A", Assert.Throws<CpfException>(() => new RpgInterpreter(host).Run(program, existingContext: context)).MessageId);
            Assert.Equal(before, Hex(context, "STATE")); Assert.Equal(1, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
        else
        {
            new RpgInterpreter(host).Run(program, existingContext: context);
            Assert.Equal(5m, Read(context, "STATE.LENGTH")); Assert.Equal("READY   ", Assert.IsType<ProgramBuffer>(Read(context, "STATE.DATA")).ToText());
            Assert.Equal(0, store.Inspect(new("QGPL", "APIQUEUE")).Entries);
        }
    }
}
