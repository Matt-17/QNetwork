namespace QNetwork.Core;

public static class TrafficRows
{
    public static IEnumerable<TrafficRow> Filter(
        IEnumerable<TrafficRow> rows,
        bool excludeZeroTotal,
        string? processNameSubstring)
    {
        IEnumerable<TrafficRow> filteredRows = rows;

        if (excludeZeroTotal)
        {
            filteredRows = filteredRows
                .Where(row => row.Total > 0);
        }

        if (!string.IsNullOrWhiteSpace(processNameSubstring))
        {
            filteredRows = filteredRows
                .Where(row => row.ProcessName.Contains(
                    processNameSubstring,
                    StringComparison.CurrentCultureIgnoreCase));
        }

        return filteredRows;
    }

    public static IEnumerable<TrafficRow> Sort(
        IEnumerable<TrafficRow> rows,
        TrafficSortColumn sortColumn)
    {
        return sortColumn switch
        {
            TrafficSortColumn.Pid => rows
                .OrderBy(row => row.Pid),
            TrafficSortColumn.ProcessName => rows
                .OrderBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Pid),
            TrafficSortColumn.Received => rows
                .OrderByDescending(row => row.Received)
                .ThenBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.Sent => rows
                .OrderByDescending(row => row.Sent)
                .ThenBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.TotalReceived => rows
                .OrderByDescending(row => row.TotalReceived)
                .ThenBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.TotalSent => rows
                .OrderByDescending(row => row.TotalSent)
                .ThenBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            _ => rows
                .OrderByDescending(row => row.Total)
                .ThenBy(row => row.ProcessName, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    public static IEnumerable<GroupedTrafficRow> GroupByProcessName(
        IEnumerable<TrafficRow> rows)
    {
        return rows
            .GroupBy(
                row => row.ProcessName,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new GroupedTrafficRow(
                group.Key,
                group.Count(),
                group.Sum(r => r.Received),
                group.Sum(r => r.Sent),
                group.Sum(r => r.TotalReceived),
                group.Sum(r => r.TotalSent)));
    }

    public static IEnumerable<GroupedTrafficRow> SortGrouped(
        IEnumerable<GroupedTrafficRow> groups,
        TrafficSortColumn sortColumn)
    {
        return sortColumn switch
        {
            TrafficSortColumn.ProcessName => groups
                .OrderBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.Received => groups
                .OrderByDescending(g => g.Received)
                .ThenBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.Sent => groups
                .OrderByDescending(g => g.Sent)
                .ThenBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.TotalReceived => groups
                .OrderByDescending(g => g.TotalReceived)
                .ThenBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            TrafficSortColumn.TotalSent => groups
                .OrderByDescending(g => g.TotalSent)
                .ThenBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase),
            _ => groups
                .OrderByDescending(g => g.Total)
                .ThenBy(g => g.ProcessName, StringComparer.CurrentCultureIgnoreCase)
        };
    }

    public static bool ShouldReplaceDisplayedRows(
        IReadOnlyCollection<TrafficRow> sampledRows,
        IReadOnlyCollection<TrafficRow> currentRows,
        bool excludeZeroTotal)
    {
        if (sampledRows.Count == 0 || currentRows.Count == 0 || !excludeZeroTotal)
            return true;

        return sampledRows.Any(row => row.Total > 0);
    }
}
