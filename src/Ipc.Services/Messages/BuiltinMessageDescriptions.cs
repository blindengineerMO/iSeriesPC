using System.Text.Json;
using Ipc.Core.Objects;
using Ipc.Services.Sqlite;

namespace Ipc.Services.Messages;

public sealed partial class MessageDescriptionStore
{
    internal void SeedSystemDescriptions()
    {
        var objects=new SqliteObjectStore(factory);
        var description=new MessageDescription("Record not added. Member &1 is full. (C I 9999)",
            "Member &1 of &3/&2 reached its record limit. Reply C to cancel, I for one additional increment, or 1 through 9999 for that many increments.",99,
            [new("*CHAR",10),new("*CHAR",10),new("*CHAR",10),new("*CHAR",10),new("*UBIN",2)],37,"0",
            ReplyRules:new("*DEC",4,SpecialValues:[new("C","0"),new("I","1")],Range:new("1","9999")));
        var initialization=new MessageDescription("Member &2 of &3/&1 reached its size limit. (C I)",
            "Reply C to cancel initialization or I to approve one additional configured increment.",99,
            [new("*CHAR",10),new("*CHAR",10),new("*CHAR",10),new("*CHAR",10),new("*UBIN",2)],37,"C",ReplyRules:new("*CHAR",1,Values:["C","I"]));
        var defaults=new Dictionary<string,MessageDescription>{{"CPA5305",description},{"CPA3138",initialization}};
        foreach(var value in defaults.Values)MessageDescriptionFormat.Validate(value);
        void AddMissing()
        {
            var existing=List("QSYS","QCPFMSG").Select(item=>item.Key).ToHashSet(StringComparer.Ordinal);
            foreach(var item in defaults.Where(item=>!existing.Contains(item.Key)))
                try{Add("QSYS","QCPFMSG",item.Key,item.Value);}catch(Ipc.Core.Messages.CpfException error)when(error.MessageId=="CPF2412"){}
        }
        if(objects.Exists("QSYS","QCPFMSG",ObjectType.MessageFile)){AddMissing();return;}
        try{objects.Create(new(){Key=new("QSYS","QCPFMSG"),ObjectType=ObjectType.MessageFile,Owner="QSYS",Ccsid=37,
            PublicAuthority=Authorities.UseBits,Description="Supported system message descriptions",
            ExtendedAttributes=new Dictionary<string,string>{{"ipc.message.builtin","1"}},
            Source=JsonSerializer.Serialize(new Payload(3,defaults),Json)});}
        catch(Microsoft.Data.Sqlite.SqliteException error)when(error.SqliteExtendedErrorCode is 1555 or 2067){AddMissing();}
    }
    public bool UsesGenericClFallback(string library,string name)
    {
        if(library!="QSYS"||name!="QCPFMSG")return false;
        var file=new SqliteObjectStore(factory).GetRequired(library,name,ObjectType.MessageFile);
        return file.ExtendedAttributes?.GetValueOrDefault("ipc.message.builtin")=="1"&&!Parse(file.Source,file.Ccsid).Messages.ContainsKey("CPF9898");
    }
}
