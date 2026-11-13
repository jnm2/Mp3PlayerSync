namespace Mp3PlayerSync.Core.Sync;

/// <summary>Shares out a track list's width between its columns, in whole pixels.</summary>
public static class ColumnWidths
{
    /// <summary>
    /// Sizes the columns that can grow and shrink, given the width their contents need (<c>Fitted</c>) and the least
    /// they may have (<c>Min</c>). When everything fits, each column gets what it needs plus a share of what's left in
    /// proportion to it, so nothing is cut off. When it doesn't, the widest columns shrink first: every column is capped
    /// at the same width, chosen so the total fits, but none goes below its minimum. A column that fits nothing gets 0.
    /// </summary>
    public static double[] Fit(double available, IReadOnlyList<(double Fitted, double Min)> columns)
    {
        var widths = new double[columns.Count];
        var total = columns.Sum(c => c.Fitted);
        if (total <= 0) return widths;

        if (available >= total)
        {
            var extra = available - total;
            for (var i = 0; i < columns.Count; i++)
                widths[i] = Math.Floor(columns[i].Fitted + extra * columns[i].Fitted / total);
            return widths;
        }

        double Width(int i, double cap) => columns[i].Fitted <= 0 ? 0 : Math.Max(columns[i].Min, Math.Min(columns[i].Fitted, cap));
        double Sum(double cap) => Enumerable.Range(0, columns.Count).Sum(i => Width(i, cap));

        double low = 0, high = columns.Max(c => c.Fitted);
        for (var step = 0; step < 50; step++)
        {
            var cap = (low + high) / 2;
            if (Sum(cap) > available) high = cap;
            else low = cap;
        }

        for (var i = 0; i < columns.Count; i++) widths[i] = Math.Floor(Width(i, low) + 1e-6);
        return widths;
    }
}
