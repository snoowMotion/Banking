using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Tests;

/// <summary>
/// Les valeurs attendues viennent de tests/reference/prototype-oracle.mjs, qui exécute
/// compute() du prototype sans modification.
/// </summary>
public class CalculatorTests
{
    private static void Near(decimal expected, decimal actual, string what = "") =>
        Assert.True(Math.Abs(expected - actual) < 0.0001m, $"{what}: attendu {expected}, obtenu {actual}");

    [Fact]
    public void Seed_matches_prototype_compute()
    {
        var r = BudgetCalculator.Compute(BudgetDefaults.Settings(), BudgetDefaults.Categories());

        Near(2998m, r.Income, "revenus");
        Near(1396m, r.FixedMonthly, "charges fixes mensuelles");
        Near(152.3333333333m, r.MandatoryProvisions, "provisions obligatoires");
        Near(1548.3333333333m, r.FixedTotal, "fixes + provisions");
        Near(1449.6666666667m, r.Envelope, "enveloppe");
        Assert.Equal(88m, r.AllocatedPercent);
        Assert.Equal(12m, r.SavingsPercent);
        Near(173.96m, r.SavingsAmount, "épargne courante");
        Near(1101.7466666667m, r.VariableTotal, "variable");
        Near(173.96m, r.PotsTotal, "pots");
        Near(1753m, r.CurrentVariable, "dépenses actuelles");
        Near(-303.3333333333m, r.CurrentBalance, "solde sans changement");
        Near(250m, r.MonthlyBonus, "primes / mois");
    }

    [Theory]
    [InlineData("courses", 681.3433333333)]
    [InlineData("resto", 101.4766666667)]
    [InlineData("carbu", 72.4833333333)]
    [InlineData("shop", 144.9666666667)]
    [InlineData("jeux", 57.9866666667)]
    [InlineData("divers", 43.49)]
    [InlineData("voiture", 86.98)]
    [InlineData("brico", 43.49)]
    [InlineData("vac", 43.49)]
    public void Seed_allocations_match_prototype(string id, double expected)
    {
        var r = BudgetCalculator.Compute(BudgetDefaults.Settings(), BudgetDefaults.Categories());
        Near((decimal)expected, r.Amounts[id], id);
    }

    [Fact]
    public void Seed_matches_the_spec_figures_when_rounded_to_the_euro()
    {
        // §8 de la spec : enveloppe 1 450, épargne 174, virement LDDS 326.
        // Le carburant y est donné à 73 (5 % de 1 450 arrondi) ; le prototype, lui, affiche 72.
        var r = BudgetCalculator.Compute(BudgetDefaults.Settings(), BudgetDefaults.Categories());
        Assert.Equal(1450m, Math.Round(r.Envelope));
        Assert.Equal(174m, Math.Round(r.SavingsAmount));
        Assert.Equal(326m, Math.Round(r.LddsTransfer));
        Assert.Equal(681m, Math.Round(r.Amounts["courses"]));
        Assert.Equal(72m, Math.Round(r.Amounts["carbu"]));
    }

    [Fact]
    public void Settled_arkea_removes_70_from_fixed_charges()
    {
        // Une charge arrêtée n'est plus dans les catégories actives de la période.
        var r = BudgetCalculator.Compute(BudgetDefaults.Settings(), BudgetDefaults.Categories().Where(c => c.Id != "arkea").ToList());

        Near(1326m, r.FixedMonthly);
        Near(1519.6666666667m, r.Envelope);
        Near(182.36m, r.SavingsAmount);
        Near(-233.3333333333m, r.CurrentBalance);
    }

    [Fact]
    public void Savings_is_what_remains_of_the_percentages()
    {
        var cats = BudgetDefaults.Categories();
        cats.Single(c => c.Id == "shop").Percentage += 10;
        var r = BudgetCalculator.Compute(BudgetDefaults.Settings(), cats);
        Assert.Equal(2m, r.SavingsPercent);
        Near(r.Envelope * 0.02m, r.SavingsAmount);
    }

    [Fact]
    public void Provisions_are_not_counted_as_allocations()
    {
        var cats = BudgetDefaults.Categories();
        Assert.All(cats.Where(c => c.Annual), c => Assert.False(c.IsAllocation));
        Assert.Equal(88m, cats.Where(c => c.IsAllocation).Sum(c => c.Percentage));
    }
}
