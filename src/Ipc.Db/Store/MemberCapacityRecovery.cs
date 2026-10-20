using System.Buffers.Binary;
using System.Globalization;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Text;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;
using Ipc.Services.Work;
using Microsoft.Data.Sqlite;

namespace Ipc.Db.Store;

public sealed partial class SqliteFileStore
{
    private sealed class MemberFullRequest(string library,string file,string member,string identity,string signature,string format,long requested,long capacity,bool initialization=false):Exception
    {
        internal string Library=>library;internal string File=>file;internal string Member=>member;internal string Identity=>identity;
        internal string Signature=>signature;internal string Format=>format;internal long Requested=>requested;internal long Capacity=>capacity;
        internal bool Initialization=>initialization;internal string InquiryId=>initialization?"CPA3138":"CPA5305";internal string CancelId=>initialization?"CPF3140":"CPF5030";
    }
    private static string CapacitySignature(FileMemberSize size)=>FormattableString.Invariant($"{size.InitialRecords}/{size.IncrementRecords}/{size.MaximumIncrements}/{size.Unlimited}");
    private static long EffectiveMemberCapacity(SqliteConnection connection,SqliteTransaction transaction,string library,string file,string member,FileDefinition definition)
    {
        var size=definition.MemberSize??new();size.Validate();
        var id=RecordSlots.Identity(connection,transaction,library,file,member)??throw LogicalError("Physical member identity not found.");
        return MemberCapacity.Read(connection,transaction,id,CapacitySignature(size),size.Capacity);
    }
    private static void RequireMemberCapacity(SqliteConnection connection,SqliteTransaction transaction,string library,string file,string member,FileDefinition definition,long number)
    {
        var capacity=EffectiveMemberCapacity(connection,transaction,library,file,member,definition);
        if(number<=capacity)return;
        var id=RecordSlots.Identity(connection,transaction,library,file,member)!;
        throw new MemberFullRequest(library,file,member,id,CapacitySignature(definition.MemberSize??new()),CursorFingerprint(definition),number,capacity);
    }
    private void ValidateCapacityTarget(string library,string file,string member,MemberFullRequest request)
    {
        library=ResolveLibrary(library).ToUpperInvariant();file=file.ToUpperInvariant();member=member.ToUpperInvariant();
        Authorize(library,file,AuthorityBit.Add);
        var definition=GetDefinition(library,file)??throw new CpfException("CPF9801","File not found.");
        if(definition.Logical is { } logical)
        {
            if(logical.SourceLibrary!=request.Library||logical.SourceFile!=request.File||!logical.Members.TryGetValue(member,out var members)||members.Count!=1||members[0]!=request.Member)
                throw LogicalError("Logical output binding changed while awaiting the size reply.");
        }
        else if(library!=request.Library||file!=request.File||member!=request.Member)throw LogicalError("Output member changed while awaiting the size reply.");
    }
    private T WithCapacityRecovery<T>(Func<CancellationToken,T> operation,CancellationToken cancellationToken,Action<MemberFullRequest>? validate=null,
        Action<MemberFullRequest,SqliteConnection?,SqliteTransaction?>? authorizeTarget=null)
    {
        using var lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_factory.ShutdownToken);
        while(true)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            try{JobExecutionBudget.Checkpoint();return operation(lifetime.Token);}
            catch(MemberFullRequest request)
            {
                if(OperationIdentity.Current?.Job is not { } job)throw new CpfException("CPF5030","Member SIZE is exhausted; an executing job is required for operator recovery.");
                validate?.Invoke(request);new JobDataAreaStore(_factory).RequireOwner(job);
                if(authorizeTarget is null)Authorize(request.Library,request.File,AuthorityBit.Add);else authorizeTarget(request,null,null);
                RequireUnenlisted(request.Library,request.File,"Member size extension");
                var increments=AskForMemberCapacity(request,job,lifetime.Token,authorizeTarget is not null);
                if(increments==0)throw new CpfException(request.CancelId,request.Initialization?"Member initialization was canceled.":"Record addition was canceled because the member is full.");
                validate?.Invoke(request);GrantMemberCapacity(request,job,increments,lifetime.Token,()=>validate?.Invoke(request),authorizeTarget);
            }
        }
    }
    private int AskForMemberCapacity(MemberFullRequest request,JobKey job,CancellationToken cancellationToken,bool authorizedMetadata=false)
    {
        var shutdown=_factory.ShutdownToken;
        var store=new MessageQueueStore(_factory);var jobs=new JobService(_factory);
        var replies=store.OpenMemberSizeReplyQueue(job,cancellationToken);
        try
        {
            FileDefinition? current;
            if(authorizedMetadata){using var metadata=_factory.Open();current=ReadDefinition(metadata,null,request.Library,request.File);}
            else current=GetDefinition(request.Library,request.File);
            if(current is null)throw new CpfException("CPF9801","Physical file not found.");
            var jobCcsid=jobs.GetRequired(job).Ccsid;var bytes=new byte[42];
            var names=new[]{request.Initialization?request.File:request.Member,request.Initialization?request.Member:request.File,new JobTemporaryLibrary(_factory).IsTemporary(request.Library)?LibraryNames.QTemp:request.Library,current.PrimaryFormat.Name};
            if(names.Any(name=>name.Length>10||name.Any(c=>!char.IsAscii(c))))throw LogicalError("Member inquiry requires native ten-character identifiers.");
            for(var index=0;index<names.Length;index++)CodePage.ToBytes(jobCcsid,names[index].PadRight(10)).CopyTo(bytes,index*10);
            using(var connection=_factory.Open())using(var command=connection.CreateCommand())
            {
                command.CommandText="SELECT mbr FROM sys_file_members WHERE lib=$lib AND name=$file AND type='*FILE' ORDER BY created,mbr";
                command.Parameters.AddWithValue("$lib",request.Library);command.Parameters.AddWithValue("$file",request.File);
                using var reader=command.ExecuteReader();var ordinal=0;var found=false;
                while(reader.Read()){ordinal++;if(reader.GetString(0)==request.Member){found=true;break;}}
                if(!found||ordinal>32767)throw LogicalError("Inquiry member is missing or outside the supported member-number range.");
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(40),checked((ushort)ordinal));
            }
            var replacement=new ProgramBuffer(bytes,jobCcsid);var descriptions=new MessageDescriptionStore(_factory);
            var snapshot=descriptions.Snapshot("QSYS","QCPFMSG",request.InquiryId,"QSYS",replacement);
            var formatted=MessageDescriptionFormat.Format(snapshot.Description,replacement,jobCcsid);
            var key=store.SendTo([new(new QualifiedName("QSYS","QSYSOPR"))],formatted.Text,"INQ",replies,messageId:request.InquiryId,severity:formatted.Severity,
                cancellationToken:cancellationToken,senderCopy:true,predefined:snapshot)[0];
            var reply=store.ReceiveFrom(replies,MessageSelection.Reply,key,cancellationToken:cancellationToken);
            if(reply is null)
            {
                jobs.SystemMessageWait(job,true);
                try{reply=store.ReceiveFrom(replies,MessageSelection.Reply,key,wait:Timeout.InfiniteTimeSpan,cancellationToken:cancellationToken);}
                finally{if(!shutdown.IsCancellationRequested)jobs.SystemMessageWait(job,false);}
            }
            var value=reply!.Data.ToText().Trim();
            if(value is "C" or "0")return 0;if(value=="I")return 1;
            if(request.Initialization)throw LogicalError("Initialization capacity recovery requires C or I.");
            return int.TryParse(value,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var count)&&count is >=1 and <=9999?count:throw LogicalError("Invalid member size reply.");
        }
        finally
        {
            if(!shutdown.IsCancellationRequested)
                try{store.CloseMemberSizeReplyQueue(job,replies);}catch(CpfException error)when(error.MessageId is "CPF1241" or "CPF9801" or "CPF9802"){}
        }
    }
    private void GrantMemberCapacity(MemberFullRequest request,JobKey job,int increments,CancellationToken cancellationToken,Action? validate,
        Action<MemberFullRequest,SqliteConnection?,SqliteTransaction?>? authorizeTarget=null)
    {
        using var operation=new JobLockStore(_factory).EnterRecordWrite(cancellationToken);
        using var connection=_factory.Open();using var transaction=SqliteCancellation.BeginWrite(connection,cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();validate?.Invoke();new JobDataAreaStore(_factory).RequireOwner(job);
        if(authorizeTarget is null)Authorize(request.Library,request.File,AuthorityBit.Add);else authorizeTarget(request,connection,transaction);
        RequireUnenlisted(request.Library,request.File,"Member size extension");
        var definition=ReadDefinition(connection,transaction,request.Library,request.File)??throw new CpfException("CPF9801","Physical file not found.");
        if(RecordSlots.Identity(connection,transaction,request.Library,request.File,request.Member)!=request.Identity||CursorFingerprint(definition)!=request.Format)
            throw LogicalError("Physical member or format changed while awaiting the size reply.");
        var capacity=EffectiveMemberCapacity(connection,transaction,request.Library,request.File,request.Member,definition);
        if(capacity>=request.Requested){transaction.Commit();return;}
        var size=definition.MemberSize??new();
        if(CapacitySignature(size)!=request.Signature||size.IncrementRecords==0)throw new CpfException("CPF5030","Member SIZE changed or has no increment; repeat the request after changing SIZE.");
        var granted=request.Capacity+(long)size.IncrementRecords*increments;
        if(granted>RecordSlots.MaximumNumber)throw new CpfException("CPF5030","The requested size extension exceeds the physical record-number limit.");
        MemberCapacity.Grant(connection,transaction,request.Identity,request.Signature,Math.Max(capacity,granted));
        if(authorizeTarget is not null){authorizeTarget(request,connection,transaction);ValidateIndexPublisher(connection,transaction,cancellationToken);}
        cancellationToken.ThrowIfCancellationRequested();transaction.Commit();
    }
}
