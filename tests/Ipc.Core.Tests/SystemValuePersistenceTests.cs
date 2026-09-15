using Ipc.Console.Session;
using Ipc.Core.Messages;
using Ipc.Services;

namespace Ipc.Core.Tests;

public sealed class SystemValuePersistenceTests
{
    [Fact]
    public void Runtime_and_restarted_configuration_agree_and_invalid_changes_are_rejected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ipc-values-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var system = IpcSystem.Create(directory))
            {
                system.Start();
                var user = system.Jobs.CreateInteractive("QUSER");
                Assert.True(new CommandService(system, user).Execute("CHGSYSVAL SYSVAL(QSECURITY) VALUE(10)").IsError);
                system.Security.Profiles.SetPassword(system.Security.Profiles.Get("QSECOFR"), "ADMIN1234");
                var admin = system.Jobs.CreateInteractive("QSECOFR");
                Assert.False(new CommandService(system, admin).Execute("CHGSYSVAL SYSVAL(QSYSNAME) VALUE(NEWSYS)").IsError);
                Assert.Equal("NEWSYS", system.Config.SystemName);
                system.SetSystemValue("QCCSID", "1208");
                Assert.Equal(1208, system.Config.Ccsid);
                Assert.Throws<CpfException>(() => system.SetSystemValue("QCCSID", "999"));
                Assert.Throws<CpfException>(() => system.SetSystemValue("QSECURITY", "99"));
                Assert.Throws<CpfException>(() => system.SetSystemValue("QDATE", "2000-01-01"));
                Assert.Equal("40", system.SystemValues.Get("QSECURITY").Value);
            }
            using var reopened = IpcSystem.Create(directory);
            reopened.Start();
            Assert.Equal("NEWSYS", reopened.Config.SystemName);
            Assert.Equal(1208, reopened.Config.Ccsid);
            Assert.Equal("1208", reopened.SystemValues.Get("QCCSID").Value);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
}
