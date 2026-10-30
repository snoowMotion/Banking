namespace BudgetTenable.Core.Budget;

public class IncomeSettings
{
    public decimal Net { get; set; } = 2050;
    public decimal Partner { get; set; } = 750;
    public decimal Caf { get; set; } = 198;
    public decimal Total => Net + Partner + Caf;
}

/// <summary>Réglages du budget (hors catégories, dont les montants et pourcentages vivent sur <c>Category</c>).</summary>
public class BudgetSettings
{
    public IncomeSettings Income { get; set; } = new();
    public decimal AnnualBonus { get; set; } = 3000;
    public bool IncludeBonus { get; set; } = true;
    public bool ArkeaSettled { get; set; }
    /// <summary>Jour de début de période, de 1 à 28.</summary>
    public int StartDay { get; set; } = 28;
    /// <summary>Première période de virement (yyyy-MM).</summary>
    public string PotStart { get; set; } = "2026-11";
    public Dictionary<string, decimal> PotOpening { get; set; } = new();
    /// <summary>Périodes (yyyy-MM) pour lesquelles le virement vers le LDDS est fait.</summary>
    public Dictionary<string, bool> LddsDone { get; set; } = new();
}
