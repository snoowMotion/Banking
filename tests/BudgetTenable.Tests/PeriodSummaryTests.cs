using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Tests;

public class PeriodSummaryTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static readonly PeriodKey Oct = PeriodKey.Parse("2026-10");
    private static void Near(decimal expected, decimal? actual, string what = "") =>
        Assert.True(actual is not null && Math.Abs(expected - actual.Value) < 0.0001m, $"{what}: attendu {expected}, obtenu {actual}");

    private static PeriodSummary Run(IEnumerable<SpendEntry> entries, string today, string key = "2026-10", BudgetSettings? s = null) =>
        PeriodSummaryCalculator.Calculate(s ?? BudgetDefaults.Settings(), BudgetDefaults.Categories(), entries.ToList(), PeriodKey.Parse(key), D(today));

    [Fact]
    public void Current_period_tracks_spending_pace()
    {
        // Le 30 octobre : 3e jour de la période 28 oct. → 27 nov. (31 jours).
        var s = Run(new[]
        {
            new SpendEntry(D("2026-10-28"), "courses", -100m),
            new SpendEntry(D("2026-10-29"), "resto", -50m),
            new SpendEntry(D("2026-10-30"), "courses", -30m),
        }, "2026-10-30");

        Assert.True(s.IsCurrent);
        Assert.Equal(31, s.Length);
        Assert.Equal(3, s.Elapsed);
        Near(180m, s.VariableSpent, "dépensé");
        Near(1101.7466666667m, s.VariableBudget, "budget variable");
        Near(921.7466666667m, s.Rest, "reste");
        Assert.Equal(29, s.DaysLeft);                      // 31 - 3 + 1, aujourd'hui compris
        Near(921.7466666667m / 29, s.PerDay, "par jour");
        Near(180m / 3 * 31, s.Projection, "projection");
        Assert.Equal(new decimal[] { 0, 100, 150, 180 }, s.DailyCumulative);
    }

    [Fact]
    public void Balance_is_income_minus_fixed_minus_ldds_minus_variable()
    {
        var s = Run(new[] { new SpendEntry(D("2026-10-30"), "shop", -200m) }, "2026-10-30");
        Near(2998m, s.Income);
        Near(1396m, s.FixedMonthly);
        Near(326.2933333333m, s.Provisions, "virement LDDS");
        Near(2998m - 1396m - 326.2933333333m - 200m, s.Balance, "bilan");
    }

    [Fact]
    public void A_refund_reduces_the_category_spending()
    {
        var s = Run(new[]
        {
            new SpendEntry(D("2026-10-29"), "shop", -120m),
            new SpendEntry(D("2026-10-30"), "shop", 45m),   // remboursement
        }, "2026-10-30");
        Near(75m, s.Envelopes.Single(e => e.CategoryId == "shop").Spent);
        Near(75m, s.VariableSpent);
    }

    [Fact]
    public void Past_period_is_complete_and_future_has_no_pace()
    {
        var past = Run(new[] { new SpendEntry(D("2026-09-30"), "courses", -310m) }, "2026-10-30", "2026-09");
        Assert.True(past.IsPast);
        Assert.Equal(past.Length, past.Elapsed);
        Assert.Equal(0, past.DaysLeft);
        Assert.Null(past.PerDay);
        Near(310m, past.Projection);

        var future = Run(Array.Empty<SpendEntry>(), "2026-10-30", "2026-12");
        Assert.False(future.IsCurrent);
        Assert.False(future.IsPast);
        Assert.Equal(0, future.Elapsed);
        Assert.Null(future.Projection);
        Assert.Single(future.DailyCumulative);
    }

    [Fact]
    public void Spending_on_the_boundary_days_goes_to_the_right_period()
    {
        var entries = new[]
        {
            new SpendEntry(D("2026-10-27"), "courses", -10m),  // dernière nuit de la période de septembre
            new SpendEntry(D("2026-10-28"), "courses", -20m),  // premier jour d'octobre
            new SpendEntry(D("2026-11-27"), "courses", -40m),  // dernier jour d'octobre
            new SpendEntry(D("2026-11-28"), "courses", -80m),  // premier jour de novembre
        };
        Near(10m, Run(entries, "2026-12-01", "2026-09").VariableSpent);
        Near(60m, Run(entries, "2026-12-01", "2026-10").VariableSpent);
        Near(80m, Run(entries, "2026-12-01", "2026-11").VariableSpent);
    }

    [Fact]
    public void History_covers_six_periods_ending_with_the_selected_one()
    {
        var s = Run(new[] { new SpendEntry(D("2026-06-10"), "courses", -500m), new SpendEntry(D("2026-10-30"), "courses", -90m) }, "2026-10-30");
        Assert.Equal(6, s.History.Count);
        Assert.Equal("2026-05", s.History[0].Key);
        Assert.Equal("2026-10", s.History[5].Key);
        Near(500m, s.History[0].Spent);      // 28 mai → 27 juin contient le 10 juin
        Near(90m, s.History[5].Spent);
    }

    [Fact]
    public void Pot_balance_is_opening_plus_transfers_minus_spending_since_first_transfer()
    {
        var settings = BudgetDefaults.Settings();
        settings.PotStart = "2026-11";
        settings.PotOpening["voiture"] = 200m;
        var entries = new[]
        {
            new SpendEntry(D("2026-11-30"), "voiture", -150m),  // période de novembre
            new SpendEntry(D("2026-10-05"), "voiture", -999m),  // avant la première période de virement : ignorée
            new SpendEntry(D("2026-12-29"), "voiture", -40m),   // période de décembre
        };
        // Fin de novembre : 200 + 1 × 86,98 − 150
        Near(136.98m, Run(entries, "2026-12-01", "2026-11", settings).Pots.Single(p => p.CategoryId == "voiture").Balance, "novembre");
        // Fin de décembre : 200 + 2 × 86,98 − 190
        Near(183.96m, Run(entries, "2027-01-01", "2026-12", settings).Pots.Single(p => p.CategoryId == "voiture").Balance, "décembre");
        // Avant la première période : seulement le solde de départ
        Near(200m, Run(entries, "2026-10-30", "2026-10", settings).Pots.Single(p => p.CategoryId == "voiture").Balance, "octobre");
    }

    [Fact]
    public void Mandatory_provisions_accrue_one_twelfth_a_month_and_are_paid_by_the_pot()
    {
        var settings = BudgetDefaults.Settings();
        settings.PotStart = "2026-11";
        var entries = new[] { new SpendEntry(D("2026-12-29"), "tf", -1432m) };
        var tf = Run(entries, "2026-12-30", "2026-12", settings).Pots.Single(p => p.CategoryId == "tf");
        Near(1432m / 12, tf.Monthly);
        Near(2 * 1432m / 12 - 1432m, tf.Balance);
        Near(1432m, tf.SpentThisPeriod);
    }

    [Fact]
    public void Pot_spending_does_not_reduce_the_variable_envelope()
    {
        var s = Run(new[] { new SpendEntry(D("2026-10-30"), "voiture", -300m) }, "2026-10-30");
        Near(0m, s.VariableSpent);
        Assert.All(s.DailyCumulative, v => Assert.Equal(0m, v));
    }

    [Fact]
    public void Required_window_covers_history_and_first_transfer_period()
    {
        var s = BudgetDefaults.Settings();
        var (from, to) = PeriodSummaryCalculator.RequiredWindow(s, PeriodKey.Parse("2026-12"));
        Assert.Equal(D("2026-07-28"), from);   // 5 périodes avant décembre
        Assert.Equal(D("2027-01-28"), to);
        s.PotStart = "2026-01";
        Assert.Equal(D("2026-01-28"), PeriodSummaryCalculator.RequiredWindow(s, PeriodKey.Parse("2026-12")).From);
    }
}
