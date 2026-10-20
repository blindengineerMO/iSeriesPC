using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Core.Objects;
using Ipc.Core.Security;
using Ipc.Services.Work;
using Ipc.Terminal;

namespace Ipc.Core.Tests;

public sealed partial class InteractiveSqlTests
{
    [Fact]
    public void Revoking_read_access_closes_even_an_EOF_cached_result_before_another_key_is_handled()
    {
        Seed();_system.Security.Profiles.SetPassword(_system.Security.Profiles.Get("QUSER"),"SqlViewerUser187");
        _system.Security.Authority.Grant("QGPL","DATA",ObjectType.File,"QUSER",Authorities.UseBits);
        using var menu=new MenuController(_system,_system.Security.Profiles.Get("QUSER"));
        void Command(string text) { foreach(var character in text)menu.Handle(new(AidKey.None,Character:character));menu.Handle(new(AidKey.Enter)); }
        string Screen()=>string.Join('\n',Enumerable.Range(1,24).Select(menu.Buffer.RowText));
        Command("STRSQL");Command("SELECT ID,NAME FROM QGPL/DATA");Assert.Contains("Display Data",Screen());
        _system.Security.Authority.Grant("QGPL","DATA",ObjectType.File,"QUSER",AuthorityBit.None);
        menu.Handle(new(AidKey.Pf24));Assert.DoesNotContain("Display Data",Screen());Assert.Contains("SQL0551",Screen());
        Assert.DoesNotContain(new JobLockStore(_system.Connections).Inspect(new("QGPL","DATA",ObjectType.File)),item=>item.Job==menu.JobKey && item.Lifetime=="Query");
    }
    [Fact]
    public async Task Disconnect_interrupts_an_active_query_and_joins_cleanup_before_returning()
    {
        Seed();using var menu=new MenuController(_system,_system.Security.Profiles.Get("QSECOFR"));
        foreach(var c in "STRSQL")menu.Handle(new(AidKey.None,Character:c));menu.Handle(new(AidKey.Enter));
        var sql="SELECT COUNT(*) FROM "+string.Join(',',Enumerable.Range(0,16).Select(index=>"QGPL/DATA A"+index));
        foreach(var c in sql)menu.Handle(new(AidKey.None,Character:c));
        var query=Task.Run(()=>menu.Handle(new(AidKey.Enter)));var deadline=DateTime.UtcNow.AddSeconds(10);var locks=new JobLockStore(_system.Connections);
        while(!locks.Inspect(new("QGPL","DATA",ObjectType.File)).Any(item=>item.Job==menu.JobKey && item.Lifetime=="Query"))
        { Assert.True(DateTime.UtcNow<deadline,"Expected the active SQL query allocation.");await Task.Delay(10); }
        menu.RequestDisconnect();await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await query.WaitAsync(TimeSpan.FromSeconds(10)));menu.Dispose();
        Assert.DoesNotContain(locks.Inspect(new("QGPL","DATA",ObjectType.File)),item=>item.Job==menu.JobKey);
    }
}
