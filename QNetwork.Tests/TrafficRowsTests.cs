using QNetwork.Core;

namespace QNetwork.Tests;

[TestClass]
public sealed class TrafficRowsTests
{
    private static readonly List<TrafficRow> SampleRows =
    [
        new TrafficRow(1, "alpha", 200, 100, 2000, 1000),
        new TrafficRow(2, "beta", 0, 0, 0, 0),
        new TrafficRow(3, "Alpha", 50, 300, 500, 3000),
        new TrafficRow(4, "gamma", 150, 400, 1500, 4000),
    ];

    [TestMethod]
    public void Filter_ExcludesZeroTotal_WhenEnabled()
    {
        var result = TrafficRows.Filter(SampleRows, excludeZeroTotal: true, processNameSubstring: null)
            .ToList();
        Assert.IsTrue(result.All(r => r.Total > 0));
        Assert.IsFalse(result.Any(r => r.Pid == 2));
    }

    [TestMethod]
    public void Filter_IncludesZeroTotal_WhenDisabled()
    {
        var result = TrafficRows.Filter(SampleRows, excludeZeroTotal: false, processNameSubstring: null)
            .ToList();
        Assert.HasCount(4, result);
    }

    [TestMethod]
    public void Filter_ByProcessName_IsCaseInsensitive()
    {
        var result = TrafficRows.Filter(SampleRows, excludeZeroTotal: false, processNameSubstring: "ALPHA")
            .ToList();
        // matches "alpha" and "Alpha"
        Assert.HasCount(2, result);
    }

    [TestMethod]
    public void Filter_ByProcessName_ReturnsEmpty_WhenNoMatch()
    {
        var result = TrafficRows.Filter(SampleRows, excludeZeroTotal: false, processNameSubstring: "zzz")
            .ToList();
        Assert.IsEmpty(result);
    }

    [TestMethod]
    public void Filter_NullSubstring_ReturnsAll()
    {
        var result = TrafficRows.Filter(SampleRows, excludeZeroTotal: false, processNameSubstring: null)
            .ToList();
        Assert.HasCount(4, result);
    }

    [TestMethod]
    public void Sort_ByPid_AscendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.Pid).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsGreaterThanOrEqualTo(result[i - 1].Pid, result[i].Pid);
    }

    [TestMethod]
    public void Sort_ByProcessName_IsCaseInsensitive()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.ProcessName).ToList();
        var alphaIndices = result
            .Select((r, i) => (r, i))
            .Where(x => x.r.ProcessName.Equals("alpha", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.i)
            .ToList();
        Assert.HasCount(2, alphaIndices);
        Assert.AreEqual(1, Math.Abs(alphaIndices[0] - alphaIndices[1]));
    }

    [TestMethod]
    public void Sort_ByReceived_DescendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.Received).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].Received, result[i].Received);
    }

    [TestMethod]
    public void Sort_BySent_DescendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.Sent).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].Sent, result[i].Sent);
    }

    [TestMethod]
    public void Sort_ByTotal_DescendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.Total).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].Total, result[i].Total);
    }

    [TestMethod]
    public void Sort_ByTotalReceived_DescendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.TotalReceived).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].TotalReceived, result[i].TotalReceived);
    }

    [TestMethod]
    public void Sort_ByTotalSent_DescendingOrder()
    {
        var result = TrafficRows.Sort(SampleRows, TrafficSortColumn.TotalSent).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].TotalSent, result[i].TotalSent);
    }

    [TestMethod]
    public void GroupByProcessName_CombinesSameNameRows()
    {
        var result = TrafficRows.GroupByProcessName(SampleRows).ToList();
        // "alpha" and "Alpha" should be merged
        var alphaGroup = result.FirstOrDefault(g =>
            g.ProcessName.Equals("alpha", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(alphaGroup);
        Assert.AreEqual(2, alphaGroup.Count);
        Assert.AreEqual(250L, alphaGroup.Received);
        Assert.AreEqual(400L, alphaGroup.Sent);
        Assert.AreEqual(2500L, alphaGroup.TotalReceived);
        Assert.AreEqual(4000L, alphaGroup.TotalSent);
    }

    [TestMethod]
    public void GroupByProcessName_GroupTotal_IsReceivedPlusSent()
    {
        var result = TrafficRows.GroupByProcessName(SampleRows).ToList();
        foreach (var group in result)
            Assert.AreEqual(group.Received + group.Sent, group.Total);
    }

    [TestMethod]
    public void GroupByProcessName_SessionTotal_IsTotalReceivedPlusTotalSent()
    {
        var result = TrafficRows.GroupByProcessName(SampleRows).ToList();
        foreach (var group in result)
            Assert.AreEqual(group.TotalReceived + group.TotalSent, group.SessionTotal);
    }

    [TestMethod]
    public void SortGrouped_ByTotal_DescendingOrder()
    {
        var groups = TrafficRows.GroupByProcessName(SampleRows);
        var result = TrafficRows.SortGrouped(groups, TrafficSortColumn.Total).ToList();
        for (int i = 1; i < result.Count; i++)
            Assert.IsLessThanOrEqualTo(result[i - 1].Total, result[i].Total);
    }

    [TestMethod]
    public void ShouldReplaceDisplayedRows_ReturnsTrue_WhenSampledHasActiveRows()
    {
        var sampled = new List<TrafficRow>
        {
            new TrafficRow(1, "proc", 100, 0)
        };
        var current = new List<TrafficRow>
        {
            new TrafficRow(2, "proc2", 0, 0)
        };
        Assert.IsTrue(TrafficRows.ShouldReplaceDisplayedRows(sampled, current, excludeZeroTotal: true));
    }

    [TestMethod]
    public void ShouldReplaceDisplayedRows_ReturnsFalse_WhenSampledAllZeroAndHideIdle()
    {
        var sampled = new List<TrafficRow>
        {
            new TrafficRow(1, "proc", 0, 0)
        };
        var current = new List<TrafficRow>
        {
            new TrafficRow(2, "proc2", 100, 0)
        };
        Assert.IsFalse(TrafficRows.ShouldReplaceDisplayedRows(sampled, current, excludeZeroTotal: true));
    }

    [TestMethod]
    public void ShouldReplaceDisplayedRows_ReturnsTrue_WhenHideIdleDisabled()
    {
        var sampled = new List<TrafficRow> { new TrafficRow(1, "proc", 0, 0) };
        var current = new List<TrafficRow> { new TrafficRow(2, "proc2", 100, 0) };
        Assert.IsTrue(TrafficRows.ShouldReplaceDisplayedRows(sampled, current, excludeZeroTotal: false));
    }
}
