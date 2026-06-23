using QNetwork.Core;

namespace QNetwork.Tests;

[TestClass]
public sealed class GroupedTrafficRowTests
{
    [TestMethod]
    public void GroupedTrafficRow_Total_IsReceivedPlusSent()
    {
        var row = new GroupedTrafficRow("chrome", 3, 100, 200, 1000, 2000);
        Assert.AreEqual(300L, row.Total);
    }

    [TestMethod]
    public void GroupedTrafficRow_SessionTotal_IsTotalReceivedPlusTotalSent()
    {
        var row = new GroupedTrafficRow("chrome", 3, 100, 200, 1000, 2000);
        Assert.AreEqual(3000L, row.SessionTotal);
    }

    [TestMethod]
    public void GroupedTrafficRow_Count_ReflectsNumberOfPids()
    {
        var row = new GroupedTrafficRow("explorer", 5, 0, 0, 0, 0);
        Assert.AreEqual(5, row.Count);
    }

    [TestMethod]
    public void GroupedTrafficRow_Record_Equality_WorksByValue()
    {
        var a = new GroupedTrafficRow("proc", 2, 100, 200, 1000, 2000);
        var b = new GroupedTrafficRow("proc", 2, 100, 200, 1000, 2000);
        Assert.AreEqual(a, b);
    }
}
