using QNetwork.Core;

namespace QNetwork.Tests;

[TestClass]
public sealed class TrafficRowTests
{
    [TestMethod]
    public void Total_IsReceivedPlusSent()
    {
        var row = new TrafficRow(1, "test", 100, 200);
        Assert.AreEqual(300L, row.Total);
    }

    [TestMethod]
    public void SessionTotal_IsTotalReceivedPlusTotalSent()
    {
        var row = new TrafficRow(1, "test", 100, 200, 1000, 2000);
        Assert.AreEqual(3000L, row.SessionTotal);
    }

    [TestMethod]
    public void SessionTotal_DefaultsToZero_WhenNotProvided()
    {
        var row = new TrafficRow(1, "test", 100, 200);
        Assert.AreEqual(0L, row.SessionTotal);
    }

    [TestMethod]
    public void Total_IsZero_WhenBothZero()
    {
        var row = new TrafficRow(1, "test", 0, 0);
        Assert.AreEqual(0L, row.Total);
    }

    [TestMethod]
    public void Record_Equality_WorksByValue()
    {
        var a = new TrafficRow(1, "proc", 10, 20, 100, 200);
        var b = new TrafficRow(1, "proc", 10, 20, 100, 200);
        Assert.AreEqual(a, b);
    }

    [TestMethod]
    public void Record_Equality_FailsOnDifferentPid()
    {
        var a = new TrafficRow(1, "proc", 10, 20);
        var b = new TrafficRow(2, "proc", 10, 20);
        Assert.AreNotEqual(a, b);
    }
}
