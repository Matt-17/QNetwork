using QNetwork.Core;

namespace QNetwork.Tests;

[TestClass]
public sealed class ProcessInfoResolverTests
{
    [TestMethod]
    public void Resolve_SystemIdlePid_ReturnsSystemIdle()
    {
        ProcessInfo info = ProcessInfoResolver.Resolve(0);
        Assert.AreEqual(0, info.Pid);
        Assert.AreEqual("System/Idle", info.ProcessName);
        Assert.IsNull(info.ExecutablePath);
    }

    [TestMethod]
    public void Resolve_CurrentProcess_ReturnsValidInfo()
    {
        int pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        ProcessInfo info = ProcessInfoResolver.Resolve(pid);
        Assert.AreEqual(pid, info.Pid);
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.ProcessName));
    }

    [TestMethod]
    public void Resolve_InvalidPid_ReturnsUnknown()
    {
        ProcessInfo info = ProcessInfoResolver.Resolve(int.MaxValue);
        Assert.AreEqual(int.MaxValue, info.Pid);
        Assert.AreEqual("Unknown", info.ProcessName);
        Assert.IsNull(info.ExecutablePath);
    }

    [TestMethod]
    public void ProcessInfo_Record_HasExpectedProperties()
    {
        var info = new ProcessInfo(
            42, "myapp", @"C:\app.exe", DateTime.Now, "My App", "Acme Corp");
        Assert.AreEqual(42, info.Pid);
        Assert.AreEqual("myapp", info.ProcessName);
        Assert.AreEqual(@"C:\app.exe", info.ExecutablePath);
        Assert.AreEqual("My App", info.FileDescription);
        Assert.AreEqual("Acme Corp", info.Company);
    }
}
