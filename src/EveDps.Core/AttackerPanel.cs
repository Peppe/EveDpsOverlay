namespace EveDps.Core;

/// <summary>
/// One rendered row of the phase-8 attackers panel: the merged display model
/// (name rows plus the optional trailing Others row), UI-free so the merge
/// rules are unit-testable in Core. <see cref="Dimmed"/> marks miss-only rows.
/// </summary>
public sealed record PanelRow(string Name, double IncomingDps, int Misses)
{
    public bool Dimmed => IncomingDps <= 0;
}

public static class AttackerPanel
{
    public const string OthersLabel = "Others";

    /// <summary>
    /// Phase 8 display budget for one window: damaging rows keep the
    /// snapshot's dps-descending order, miss-only rows rank below them by
    /// miss count (descending, name tie-break). When more rows than
    /// <paramref name="budget"/> exist, every row from rank
    /// <c>budget-1</c> on merges into a single trailing row labeled
    /// "Others" (dps and misses summed; dimmed only when ALL its tail rows
    /// are miss-only). Exactly ≤ budget rows never produce an Others row.
    /// </summary>
    public static IReadOnlyList<PanelRow> DisplayRows(IReadOnlyList<AttackerRow> attackers, int budget)
    {
        var damaging = attackers.Where(a => a.IncomingDps > 0)
            .Select(a => new PanelRow(a.Name, a.IncomingDps, a.Misses))
            .ToList();
        var missOnly = attackers.Where(a => a.IncomingDps <= 0 && a.Misses > 0)
            .OrderByDescending(a => a.Misses).ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => new PanelRow(a.Name, a.IncomingDps, a.Misses))
            .ToList();

        var display = new List<PanelRow>(damaging.Count + missOnly.Count + 1);
        display.AddRange(damaging);
        display.AddRange(missOnly);

        if (display.Count > budget)
        {
            var tail = display.Skip(budget - 1).ToList();
            display = [.. display.Take(budget - 1)];
            display.Add(new PanelRow(
                OthersLabel,
                tail.Sum(r => r.IncomingDps),
                tail.Sum(r => r.Misses)));
        }
        return display;
    }
}
