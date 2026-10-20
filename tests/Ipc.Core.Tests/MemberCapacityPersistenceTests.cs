using Ipc.Core.Objects;
using Ipc.Core.Work;
using Ipc.Db.Definitions;
using Ipc.Db.Store;
using Ipc.Services;
using Ipc.Services.Events;
using Ipc.Services.Messages;
using Ipc.Services.Sqlite;

namespace Ipc.Core.Tests;

public sealed class MemberCapacityPersistenceTests:IDisposable
{
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"ipc-capacity-runtime-"+Guid.NewGuid().ToString("N"));
    private static void Seed(IpcSystem system)
    {
        system.Start();system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"),"CapacityRestartAdmin41");var files=new SqliteFileStore(system.Connections,system.Objects);
        var format=new RecordFormat{Name="ROW",Fields=[new(){Name="ID",Type=FieldType.Binary,Length=8}]};format.AssignPositions();
        files.CreatePhysicalFile("QGPL","DATA",new(){Name="DATA",Attribute=FileAttribute.Physical,Formats=[format],MemberSize=new(1,1,1)},"");Add(files,1);Add(files,2);
    }
    private static void Add(SqliteFileStore files,long value)=>files.Insert("QGPL","DATA","DATA","ROW",new Dictionary<string,object?>{["ID"]=value});
    [Fact]
    public void Successful_grants_are_usable_after_system_restart_without_another_reply()
    {
        using(var system=IpcSystem.Create(_directory))
        {
            Seed(system);new SystemReplyList(system.Connections).Put(new(1,"CPA5305",Reply:"2"));var job=system.Jobs.CreateInteractive("QSECOFR");new InquiryReplyPolicy(system.Connections).Set(job.Key,"*SYSRPYL");
            using var owner=OperationIdentity.Enter("QSECOFR",job.Key);Add(new(system.Connections,system.Objects),3);
        }
        using var reopened=IpcSystem.Create(_directory);reopened.Start();var files=new SqliteFileStore(reopened.Connections,reopened.Objects);Add(files,4);Assert.Equal(4,files.RowCount("QGPL","DATA","DATA"));
    }
    [Fact]
    public async Task Factory_shutdown_stops_the_wait_and_interrupted_job_recovery_closes_its_registered_inquiry()
    {
        using var system=IpcSystem.Create(_directory);Seed(system);using var factory=new SqliteConnectionFactory(_directory);var files=new SqliteFileStore(factory,system.Objects);
        var job=system.Jobs.CreateInteractive("QSECOFR");var messages=new MessageQueueStore(system.Connections);var queue=new QualifiedName("QSYS","QSYSOPR");
        var work=Task.Run(()=>{using var owner=OperationIdentity.Enter("QSECOFR",job.Key);return Record.Exception(()=>Add(files,3));});
        try
        {
            var deadline=DateTime.UtcNow.AddSeconds(10);while(!messages.List(queue).Any(message=>message.MessageId=="CPA5305"&&!message.Replied)){Assert.True(DateTime.UtcNow<deadline);await Task.Delay(20);}
            factory.Dispose();var error=await work.WaitAsync(TimeSpan.FromSeconds(10));Assert.True(error is OperationCanceledException or ObjectDisposedException,error?.ToString());
            system.Jobs.Complete(job.Key,JobCompletion.Abnormal,outcome:JobExecutionState.Interrupted);Assert.All(messages.List(queue),message=>Assert.True(message.Replied));
            Assert.Equal(2,new SqliteFileStore(system.Connections,system.Objects).RowCount("QGPL","DATA","DATA"));
        }
        finally{factory.Dispose();await work.WaitAsync(TimeSpan.FromSeconds(10));}
    }
    [Fact]
    public void Repeated_file_copies_release_staging_on_pooled_disk_connections()
    {
        using var system=IpcSystem.Create(_directory);Seed(system);var files=new SqliteFileStore(system.Connections,system.Objects);
        Assert.Equal(2,files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","DATA"),"DATA",replace:true));
        Assert.Equal(2,files.CopyRecords(new("QGPL","DATA"),"DATA",new("QGPL","DATA"),"DATA",replace:true));Assert.Equal(2,files.RowCount("QGPL","DATA","DATA"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Startup_adds_missing_initialization_inquiries_without_replacing_custom_descriptions(bool existing)
    {
        long objects;
        using (var system = IpcSystem.Create(_directory))
        {
            Seed(system); objects = system.Objects.ObjectCount;
            var descriptions = new MessageDescriptionStore(system.Connections);
            descriptions.Change("QSYS", "QCPFMSG", "CPA5305", text: "Customized member full message");
            if (existing) descriptions.Change("QSYS", "QCPFMSG", "CPA3138", text: "Customized initialization message");
            else descriptions.Remove("QSYS", "QCPFMSG", "CPA3138");
        }
        using var reopened = IpcSystem.Create(_directory); reopened.Start();
        var restored = new MessageDescriptionStore(reopened.Connections);
        Assert.Equal("Customized member full message", restored.Get("QSYS", "QCPFMSG", "CPA5305"));
        var initialization = restored.GetDescription("QSYS", "QCPFMSG", "CPA3138");
        Assert.Equal(existing ? "Customized initialization message" : "Member &2 of &3/&1 reached its size limit. (C I)", initialization.Text);
        Assert.Equal("C", initialization.DefaultReply);
        Assert.Equal(objects, reopened.Objects.ObjectCount);
    }
    public void Dispose(){if(Directory.Exists(_directory))Directory.Delete(_directory,true);}
}
