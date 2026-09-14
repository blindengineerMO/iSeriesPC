using System.Reflection;

namespace Ipc.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Test_run_smoke()
    {
        Assert.True(true);
    }

    [Fact]
    public void Core_assembly_loads()
    {
        var asm = Assembly.Load("Ipc.Core");
        Assert.Contains("Ipc.Core", asm.GetName().Name);
    }
}