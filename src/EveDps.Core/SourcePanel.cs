namespace EveDps.Core;

/// <summary>
/// One rendered row of a phase-8/11 sources panel: the merged display model
/// (name rows plus the optional trailing Others row), UI-free so the merge
/// rules are unit-testable in Core. <see cref="Dimmed"/> marks miss-only rows.
/// Phase 11: <see cref="Em"/>/<see cref="Thermal"/>/<see cref="Kinetic"/>/
/// <see cref="Explosive"/> carry the merged row's attributed dps so the HUD
/// can draw type-colored bars (the Others merge sums the slices too).
/// </summary>
public sealed record PanelRow(
    string Name, double Dps, int Misses,
    double Em = 0, double Thermal = 0, double Kinetic = 0, double Explosive = 0)
{
    public bool Dimmed => Dps <= 0;
}

public static class SourcePanel
{
    public const string OthersLabel = "Others";

    /// <summary>
    /// Phase 8 display budget for one window: damaging rows keep the
    /// snapshot's dps-descending order, miss-only rows rank below them by
    /// miss count (descending, name tie-break). When more rows than
    /// <paramref name="budget"/> exist, every row from rank
    /// <c>budget-1</c> on merges into a single trailing row labeled
    /// "Others" (dps, misses and type slices summed; dimmed only when ALL
    /// its tail rows are miss-only). Exactly = budget rows never produce an
    /// Others row. Shared by the OUT (weapons) and IN (attackers) panels.
    /// </summary>
    public static IReadOnlyList<PanelRow> DisplayRows(IReadOnlyList<SourceRow> sources, int budget)
    {
        var damaging = sources.Where(s => s.Dps > 0)
            .Select(s => new PanelRow(s.Name, s.Dps, s.Misses, s.Em, s.Thermal, s.Kinetic, s.Explosive))
            .ToList();
        var missOnly = sources.Where(s => s.Dps <= 0 && s.Misses > 0)
            .OrderByDescending(s => s.Misses).ThenBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new PanelRow(s.Name, s.Dps, s.Misses, s.Em, s.Thermal, s.Kinetic, s.Explosive))
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
                tail.Sum(r => r.Dps),
                tail.Sum(r => r.Misses),
                tail.Sum(r => r.Em), tail.Sum(r => r.Thermal),
                tail.Sum(r => r.Kinetic), tail.Sum(r => r.Explosive)));
        }
        return display;
    }
}
