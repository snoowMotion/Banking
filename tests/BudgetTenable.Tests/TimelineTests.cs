using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Tests;

public class TimelineTests
{
    private static PeriodKey K(string s) => PeriodKey.Parse(s);
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static void Near(decimal expected, decimal actual, string what = "") =>
        Assert.True(Math.Abs(expected - actual) < 0.0001m, $"{what}: attendu {expected}, obtenu {actual}");

    private static BudgetTimeline With(IEnumerable<CategoryTerm>? terms = null, IEnumerable<IncomeTerm>? income = null, IEnumerable<Category>? extra = null)
    {
        var cats = BudgetDefaults.Categories().Concat(extra ?? Array.Empty<Category>());
        return new BudgetTimeline(BudgetDefaults.Settings(), cats, terms ?? Array.Empty<CategoryTerm>(), income ?? Array.Empty<IncomeTerm>());
    }

    [Fact]
    public void Without_changes_every_period_has_the_seed_values()
    {
        var t = With();
        foreach (var p in new[] { "2025-01", "2026-10", "2030-06" })
            Near(1449.6666666667m, BudgetCalculator.Compute(t.At(K(p)).Settings, t.At(K(p)).Categories).Envelope, p);
    }

    [Fact]
    public void A_change_applies_from_its_period_and_leaves_the_past_untouched()
    {
        // L'assurance passe de 75 à 95 € à partir de la période de novembre.
        var t = With(new[] { new CategoryTerm { CategoryId = "assur", From = "2026-11", Amount = 95 } });
        Near(1449.6666666667m, BudgetCalculator.Compute(t.At(K("2026-10")).Settings, t.At(K("2026-10")).Categories).Envelope, "octobre inchangé");
        Near(1429.6666666667m, BudgetCalculator.Compute(t.At(K("2026-11")).Settings, t.At(K("2026-11")).Categories).Envelope, "novembre");
        Near(1429.6666666667m, BudgetCalculator.Compute(t.At(K("2027-06")).Settings, t.At(K("2027-06")).Categories).Envelope, "la suite");
    }

    [Fact]
    public void A_later_dated_change_overrides_an_earlier_one_only_from_its_own_period()
    {
        var t = With(new[]
        {
            new CategoryTerm { CategoryId = "tel", From = "2026-10", Amount = 100 },
            new CategoryTerm { CategoryId = "tel", From = "2027-02", Amount = 110 },
        });
        Assert.Equal(92m, t.StateOf(t.AllCategories.Single(c => c.Id == "tel"), K("2026-09")).Category.Amount);
        Assert.Equal(100m, t.StateOf(t.AllCategories.Single(c => c.Id == "tel"), K("2026-12")).Category.Amount);
        Assert.Equal(110m, t.StateOf(t.AllCategories.Single(c => c.Id == "tel"), K("2027-02")).Category.Amount);
    }

    [Fact]
    public void A_new_category_only_exists_from_its_first_period()
    {
        var extra = new Category { Id = "cantine", Label = "Cantine", Group = CategoryGroup.Fixe, Amount = 45, ActiveFrom = "2026-11", Order = 99 };
        var t = With(extra: new[] { extra });
        Assert.DoesNotContain(t.At(K("2026-10")).Categories, c => c.Id == "cantine");
        Assert.Contains(t.At(K("2026-11")).Categories, c => c.Id == "cantine");
        Near(1396m, BudgetCalculator.Compute(t.At(K("2026-10")).Settings, t.At(K("2026-10")).Categories).FixedMonthly, "avant");
        Near(1441m, BudgetCalculator.Compute(t.At(K("2026-11")).Settings, t.At(K("2026-11")).Categories).FixedMonthly, "après");
    }

    [Fact]
    public void An_archived_category_stops_at_its_period_and_can_come_back()
    {
        var t = With(new[]
        {
            new CategoryTerm { CategoryId = "arkea", From = "2026-12", Amount = 70, Active = false },
            new CategoryTerm { CategoryId = "arkea", From = "2027-03", Amount = 70, Active = true },
        });
        bool Has(string p) => t.At(K(p)).Categories.Any(c => c.Id == "arkea");
        Assert.True(Has("2026-11"));
        Assert.False(Has("2026-12"));
        Assert.False(Has("2027-02"));
        Assert.True(Has("2027-03"));
        Assert.Equal("2026-12", t.StateOf(t.AllCategories.Single(c => c.Id == "arkea"), K("2027-01")).EndedFrom);
    }

    [Fact]
    public void Income_changes_are_dated_too()
    {
        var t = With(income: new[] { new IncomeTerm { From = "2026-11", Net = 2200, Partner = 750, Caf = 198, AnnualBonus = 3000 } });
        Assert.Equal(2998m, t.IncomeAt(K("2026-10")).Total);
        Assert.Equal(3148m, t.IncomeAt(K("2026-11")).Total);
    }

    [Fact]
    public void A_dated_budget_change_in_the_current_period_counts_this_month_but_not_the_past()
    {
        // L'assurance passe de 75 à 105 € à partir de la période en cours : l'enveloppe baisse de 30 € ce mois-ci seulement.
        var t = With(new[] { new CategoryTerm { CategoryId = "assur", From = "2026-10", Amount = 105 } });
        var cur = PeriodSummaryCalculator.Calculate(t, Array.Empty<SpendEntry>(), K("2026-10"), D("2026-11-02"));
        var past = PeriodSummaryCalculator.Calculate(t, Array.Empty<SpendEntry>(), K("2026-09"), D("2026-11-02"));
        Near(0.76m * 1419.6666666667m, cur.VariableBudget, "budget variable de la période en cours");
        Near(0.76m * 1449.6666666667m, past.VariableBudget, "budget variable de la période passée, inchangé");
        Near(1426m, cur.FixedMonthly, "charges fixes de la période en cours");
        Near(1396m, past.FixedMonthly, "charges fixes de la période passée");
    }

    [Fact]
    public void History_keeps_each_periods_own_budget_and_pot_balances_do_not_move_when_a_percentage_changes_later()
    {
        // Le virement du pot voiture passe de 6 % à 10 % à partir de décembre : les soldes d'avant ne changent pas.
        var settings = BudgetDefaults.Settings();
        settings.PotStart = "2026-11";
        var plain = new BudgetTimeline(settings, BudgetDefaults.Categories(), Array.Empty<CategoryTerm>(), Array.Empty<IncomeTerm>());
        var changed = new BudgetTimeline(settings, BudgetDefaults.Categories(),
            new[] { new CategoryTerm { CategoryId = "voiture", From = "2026-12", Percentage = 10 } }, Array.Empty<IncomeTerm>());

        decimal Balance(BudgetTimeline t, string p) => PeriodSummaryCalculator.Calculate(t, Array.Empty<SpendEntry>(), K(p), D("2027-01-10")).Pots.Single(x => x.CategoryId == "voiture").Balance;
        Near(Balance(plain, "2026-11"), Balance(changed, "2026-11"), "novembre inchangé");
        Assert.True(Balance(changed, "2026-12") > Balance(plain, "2026-12"));
        Near(86.98m + 144.9666666667m, Balance(changed, "2026-12"), "décembre = 6 % + 10 % de l'enveloppe");

        var s = PeriodSummaryCalculator.Calculate(changed, Array.Empty<SpendEntry>(), K("2026-12"), D("2027-01-10"));
        Assert.All(s.History, h => Assert.True(h.Budget > 0));
    }

    [Fact]
    public void Spending_in_a_category_that_is_not_active_in_the_period_is_still_counted_and_shown()
    {
        var extra = new Category { Id = "sport", Label = "Sport", Group = CategoryGroup.Variable, Percentage = 2, ActiveFrom = "2026-12", Order = 99 };
        var t = With(extra: new[] { extra });
        var s = PeriodSummaryCalculator.Calculate(t, new[] { new SpendEntry(D("2026-10-30"), "sport", -40m) }, K("2026-10"), D("2026-11-02"));
        Near(40m, s.VariableSpent, "dépensé");
        var line = s.Envelopes.Single(e => e.CategoryId == PeriodSummaryCalculator.InactiveEnvelopeId);
        Near(40m, line.Spent);
        Assert.Equal(0m, line.Budget);
    }
}
