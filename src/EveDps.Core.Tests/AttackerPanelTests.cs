using EveDps.Core;

namespace EveDps.Core.Tests;

public class AttackerPanelTests
{
    private static AttackerRow Hit(string name, double dps) => new(name, dps, 0);
    private static AttackerRow Miss(string name, int misses) => new(name, 0, misses);

    [Fact]
    public void Six_attackers_merge_into_four_names_plus_Others()
    {
        var rows = new[]
        {
            Hit("A", 10), Hit("B", 8), Hit("C", 6), Hit("D", 4), Hit("E", 2), Hit("F", 1),
        };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        Assert.Equal(5, display.Count);
        Assert.Equal(["A", "B", "C", "D", "Others"], display.Select(r => r.Name));
        Assert.Equal(2 + 1, display[^1].IncomingDps); // ranks 5+ summed
        Assert.False(display[^1].Dimmed);
    }

    [Fact]
    public void Exactly_five_rows_stay_five_name_rows()
    {
        var rows = new[] { Hit("A", 5), Hit("B", 4), Hit("C", 3), Hit("D", 2), Hit("E", 1) };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        Assert.Equal(["A", "B", "C", "D", "E"], display.Select(r => r.Name));
    }

    [Fact]
    public void Others_row_always_sits_last_even_when_it_would_lead()
    {
        // Many small attackers whose rank-5+ dps sum beats rank 1:
        var rows = new[]
        {
            Hit("A", 10),
            Hit("B", 9), Hit("C", 9), Hit("D", 9), Hit("E", 9),
            Hit("F", 8), Hit("G", 8),
        };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        // Ordering is name rows first (in the passed order — Core's snapshot
        // is already dps-descending), Others last; ranks 5+ merge (E+F+G).
        Assert.Equal(["A", "B", "C", "D", "Others"], display.Select(r => r.Name));
        Assert.Equal(9 + 8 + 8, display[^1].IncomingDps); // would "lead" but stays last
        Assert.False(display[^1].Dimmed);
    }

    [Fact]
    public void Miss_only_rows_rank_below_damaging_rows_by_miss_count()
    {
        var rows = new[]
        {
            Hit("Dmg1", 5), Miss("M3", 3), Hit("Dmg2", 1), Miss("M5", 5), Miss("M1", 1),
        };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        // Damaging (snapshot dps order) first, then miss-only by miss count
        // descending with name tie-break.
        Assert.Equal(["Dmg1", "Dmg2", "M5", "M3", "M1"], display.Select(r => r.Name));
        Assert.True(display[2].Dimmed);
        Assert.Equal(new[] { 0, 0, 5, 3, 1 }, display.Select(r => r.Misses));
    }

    [Fact]
    public void All_miss_only_window_has_no_leader_and_every_row_dimmed()
    {
        var rows = new[] { Miss("M2", 2), Miss("M7", 7) };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        Assert.All(display, r => Assert.True(r.Dimmed));
        Assert.Equal(0, display.Max(r => r.IncomingDps));
    }

    [Fact]
    public void Miss_only_tail_merges_into_a_dimmed_Others_row()
    {
        // 5 damaging + 2 miss-only ⇒ 4 names + Others(dps 2, misses 5+7).
        var rows = new[]
        {
            Hit("A", 4), Hit("B", 3), Hit("C", 2), Hit("D", 1), Hit("E", 0.5),
            Miss("M5", 5), Miss("M7", 7),
        };

        var display = AttackerPanel.DisplayRows(rows, budget: 5);

        Assert.Equal(5, display.Count);
        Assert.Equal(["A", "B", "C", "D", "Others"], display.Select(r => r.Name));
        Assert.Equal(0.5, display[^1].IncomingDps, 6);
        Assert.Equal(12, display[^1].Misses); // miss counters merge into Others too
        // A mixed tail (some damage) is NOT dimmed — dps > 0 defines the width.
        Assert.False(display[^1].Dimmed);
    }

    [Fact]
    public void Empty_window_produces_no_rows()
    {
        Assert.Empty(AttackerPanel.DisplayRows([], budget: 5));
    }
}
