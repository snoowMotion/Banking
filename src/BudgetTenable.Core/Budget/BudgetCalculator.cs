using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Budget;

public sealed record BudgetResult(
    decimal Income,
    /// <summary>Charges fixes mensuelles + douzième des provisions obligatoires.</summary>
    decimal FixedTotal,
    decimal FixedMonthly,
    /// <summary>Douzièmes mis de côté pour les provisions annuelles (taxe foncière, assurance auto).</summary>
    decimal MandatoryProvisions,
    decimal Envelope,
    decimal AllocatedPercent,
    decimal SavingsPercent,
    IReadOnlyDictionary<string, decimal> Amounts,
    decimal SavingsAmount,
    decimal VariableTotal,
    decimal PotsTotal,
    decimal CurrentVariable,
    decimal CurrentBalance,
    decimal MonthlyBonus)
{
    /// <summary>Virement mensuel vers le LDDS : provisions obligatoires + pots.</summary>
    public decimal LddsTransfer => PotsTotal + MandatoryProvisions;
}

/// <summary>Port de la fonction <c>compute()</c> du prototype, sans arrondi intermédiaire.</summary>
public static class BudgetCalculator
{
    public const string ArkeaId = "arkea";

    public static BudgetResult Compute(BudgetSettings s, IReadOnlyCollection<Category> categories)
    {
        var income = s.Income.Total;

        decimal fixedMonthly = 0, mandatory = 0;
        foreach (var c in categories)
        {
            if (c.Group == CategoryGroup.Fixe)
                fixedMonthly += c.Id == ArkeaId && s.ArkeaSettled ? 0 : c.Amount;
            else if (c.Group == CategoryGroup.Pot && c.Annual)
                mandatory += c.Amount / 12m;
        }

        var fixedTotal = fixedMonthly + mandatory;
        var envelope = income - fixedTotal;

        var alloc = categories.Where(c => c.IsAllocation).ToList();
        var allocPct = alloc.Sum(c => c.Percentage);
        var amounts = alloc.ToDictionary(c => c.Id, c => envelope * c.Percentage / 100m);
        var savingsPct = 100m - allocPct;

        var variable = alloc.Where(c => c.Group == CategoryGroup.Variable).Sum(c => amounts[c.Id]);
        var pots = alloc.Where(c => c.Group == CategoryGroup.Pot).Sum(c => amounts[c.Id]);
        var currentVar = alloc.Sum(c => c.CurrentAverage);

        return new BudgetResult(
            income, fixedTotal, fixedMonthly, mandatory, envelope, allocPct, savingsPct, amounts,
            envelope * savingsPct / 100m, variable, pots, currentVar, income - fixedTotal - currentVar,
            s.AnnualBonus / 12m);
    }

    /// <summary>Budget mensuel d'un pot : virement pour un pot normal, douzième de la facture pour une provision annuelle.</summary>
    public static decimal MonthlyAmount(Category c, BudgetResult r) =>
        c.Annual ? c.Amount / 12m : r.Amounts.GetValueOrDefault(c.Id);
}
