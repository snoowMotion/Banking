using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Budget;

public class IncomeSettings
{
    public decimal Net { get; set; } = 2050;
    public decimal Partner { get; set; } = 750;
    public decimal Caf { get; set; } = 198;
    public decimal Total => Net + Partner + Caf;
}

/// <summary>
/// Réglages du budget. Les revenus et la prime annuelle sont les valeurs de départ : leurs changements datés sont des <see cref="IncomeTerm"/>.
/// Les montants et pourcentages des catégories vivent sur <c>Category</c> et <see cref="CategoryTerm"/>.
/// </summary>
public class BudgetSettings
{
    public IncomeSettings Income { get; set; } = new();
    public decimal AnnualBonus { get; set; } = 3000;
    public bool IncludeBonus { get; set; } = true;
    /// <summary>Jour de début de période, de 1 à 28.</summary>
    public int StartDay { get; set; } = 28;
    /// <summary>Première période de virement (yyyy-MM).</summary>
    public string PotStart { get; set; } = "2026-11";
    public Dictionary<string, decimal> PotOpening { get; set; } = new();
    /// <summary>Périodes (yyyy-MM) pour lesquelles le virement vers le LDDS est fait.</summary>
    public Dictionary<string, bool> LddsDone { get; set; } = new();
}
