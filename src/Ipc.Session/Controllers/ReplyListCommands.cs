using System.Globalization;
using Ipc.Cl.Commands;
using Ipc.Cl.Parsing;
using Ipc.Core.Messages;
using Ipc.Core.Menu;
using Ipc.Core.Text;
using Ipc.Services.Messages;

namespace Ipc.Console.Session;

public sealed partial class CommandService
{
    private CommandResult ExecuteReplyList(CommandCall call)
    {
        var store=new SystemReplyList(_system.Connections);
        if(call.Name=="WRKRPYLE")return WorkResult("System reply list","WRKRPYLE",store.List().Select(entry=>new WorkRow(entry.Sequence.ToString(CultureInfo.InvariantCulture),$"{entry.Sequence,4} {entry.MessageId,-7} {entry.Reply}",[
            new("2","Change",$"CHGRPYLE SEQNBR({entry.Sequence})",Prompt:true),
            new("4","Remove",$"RMVRPYLE SEQNBR({entry.Sequence})",Confirm:true)])));
        string Value(string name,string fallback)=>CommandParser.Unquote(call.GetOption(name)??fallback);
        if(!int.TryParse(Value("SEQNBR",call.Positional.FirstOrDefault()??""),NumberStyles.None,CultureInfo.InvariantCulture,out var number)||number is <1 or >9999)
            throw new CpfException("IPC0003","SEQNBR requires 1–9999.");
        if(call.Name=="RMVRPYLE"){store.Remove(number);return CommandResult.Ok("Reply list entry removed.");}
        SystemReplyEntry Build(SystemReplyEntry? previous)
        {
        var dump=Value("DUMP","*NO").ToUpperInvariant();if(dump!="*NO"&&(dump!="*SAME"||previous is null))throw new CpfException("IPC0003","DUMP(*YES) requires the native job-dump runtime.");
        var ccsidText=Value("CCSID",previous?.Ccsid.ToString(CultureInfo.InvariantCulture)??"*JOB").ToUpperInvariant();
        var ccsid=ccsidText switch{"*JOB"=>_job?.Ccsid??_system.Config.Ccsid,"*HEX"=>65535,"*SAME"=>previous?.Ccsid??throw new CpfException("IPC0003","CCSID(*SAME) requires CHGRPYLE."),_=>int.TryParse(ccsidText,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)?parsed:throw new CpfException("IPC0003","Invalid CCSID.")};
        var comparison=previous?.Comparison;var start=previous?.Start??1;
        if(call.GetOption("CMPDTA") is { } raw && raw.Trim().ToUpperInvariant()!="*SAME")
        {
            var parts=CommandParser.Tokenize(raw);if(parts.Count is <1 or >2)throw new CpfException("IPC0003","CMPDTA requires data and an optional start position.");
            var text=CommandParser.Unquote(parts[0]);
            comparison=text=="*NONE"?null:parts[0].StartsWith("X'",StringComparison.OrdinalIgnoreCase)?Convert.FromHexString(parts[0][2..^1]):StrictJobEncoding(ccsid==65535?(_job?.Ccsid??_system.Config.Ccsid):ccsid).GetBytes(text);
            start=parts.Count==1||parts[1].ToUpperInvariant()=="*NONE"?1:int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var position)?position:throw new CpfException("IPC0003","Invalid CMPDTA position.");
        }
        string Change(string name,string fallback){var value=Value(name,fallback);return previous is not null&&value=="*SAME"?name=="MSGID"?previous.MessageId:previous.Reply:value;}
        return new(number,Change("MSGID",previous?.MessageId??"*ANY").ToUpperInvariant(),comparison,start,ccsid,Change("RPY",previous?.Reply??"*DFT"));
        }
        if(call.Name=="CHGRPYLE")store.Change(number,old=>Build(old));else store.Put(Build(null));
        return CommandResult.Ok("Reply list entry saved.");
    }
}
