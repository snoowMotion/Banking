using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Budget;

/// <summary>Une catégorie telle qu'elle est à une période donnée : valeurs en vigueur, et si elle existe encore.</summary>
public sealed record CategoryState(Category Category, bool Active, string? EndedFrom);

public sealed record ResolvedBudget(BudgetSettings Settings, IReadOnlyList<Category> Categories);

/// <summary>
/// Valeurs du budget période par période. Chaque catégorie a des valeurs de départ (celles de l'entité), éventuellement
/// remplacées à partir d'une période par un <see cref="CategoryTerm"/> ; il en va de même des revenus (<see cref="IncomeTerm"/>).
/// Modifier le budget « à partir de P » n'affecte donc que P et la suite : l'historique ne bouge pas.
/// </summary>
public sealed class BudgetTimeline
{
    private readonly BudgetSettings _base;
    private readonly IReadOnlyList<Category> _categories;
    private readonly Dictionary<string, List<(PeriodKey From, CategoryTerm Term)>> _terms = new();
    private readonly List<(PeriodKey From, IncomeTerm Term)> _income = new();
    private readonly Dictionary<PeriodKey, ResolvedBudget> _cache = new();

    public BudgetTimeline(BudgetSettings baseSettings, IEnumerable<Category> categories, IEnumerable<CategoryTerm> terms, IEnumerable<IncomeTerm> incomeTerms)
    {
        _base = baseSettings;
        _categories = categories.ToList();
        foreach (var t in terms)
        {
            if (!PeriodKey.TryParse(t.From, out var from)) continue;
            if (!_terms.TryGetValue(t.CategoryId, out var list)) _terms[t.CategoryId] = list = new();
            list.Add((from, t));
        }
        foreach (var list in _terms.Values) list.Sort((a, b) => a.From.CompareTo(b.From));
        foreach (var t in incomeTerms)
            if (PeriodKey.TryParse(t.From, out var from)) _income.Add((from, t));
        _income.Sort((a, b) => a.From.CompareTo(b.From));
    }

    /// <summary>Budget sans historique : mêmes valeurs à toutes les périodes.</summary>
    public static BudgetTimeline Static(BudgetSettings settings, IEnumerable<Category> categories) =>
        new(settings, categories, Array.Empty<CategoryTerm>(), Array.Empty<IncomeTerm>());

    public IReadOnlyList<Category> AllCategories => _categories;

    /// <summary>Réglages et catégories actives à la période (valeurs en vigueur à cette période).</summary>
    public ResolvedBudget At(PeriodKey p)
    {
        if (_cache.TryGetValue(p, out var hit)) return hit;
        var states = States(p);
        var settings = new BudgetSettings
        {
            Income = IncomeAt(p),
            AnnualBonus = AnnualBonusAt(p),
            IncludeBonus = _base.IncludeBonus,
            StartDay = _base.StartDay,
            PotStart = _base.PotStart,
            PotOpening = _base.PotOpening,
            LddsDone = _base.LddsDone,
        };
        var resolved = new ResolvedBudget(settings, states.Where(x => x.Active).Select(x => x.Category).ToList());
        _cache[p] = resolved;
        return resolved;
    }

    /// <summary>Toutes les catégories, y compris celles qui n'existent pas encore ou plus à cette période.</summary>
    public IReadOnlyList<CategoryState> States(PeriodKey p) => _categories.Select(c => StateOf(c, p)).ToList();

    public CategoryState StateOf(Category c, PeriodKey p)
    {
        var copy = new Category
        {
            Id = c.Id, Label = c.Label, Group = c.Group, Annual = c.Annual, Note = c.Note, CurrentAverage = c.CurrentAverage, Order = c.Order,
            Percentage = c.Percentage, Amount = c.Amount, AccountId = c.AccountId, ActiveFrom = c.ActiveFrom,
        };
        if (c.ActiveFrom is not null && PeriodKey.TryParse(c.ActiveFrom, out var born) && p < born)
            return new CategoryState(copy, false, null);

        var active = true;
        string? endedFrom = null;
        if (_terms.TryGetValue(c.Id, out var list))
        {
            foreach (var (from, t) in list)
            {
                if (from > p) break;
                copy.Percentage = t.Percentage;
                copy.Amount = t.Amount;
                copy.AccountId = t.AccountId;
                active = t.Active;
                endedFrom = t.Active ? null : t.From;
            }
        }
        return new CategoryState(copy, active, endedFrom);
    }

    public IncomeSettings IncomeAt(PeriodKey p)
    {
        var t = LastIncome(p);
        return t is null
            ? new IncomeSettings { Net = _base.Income.Net, Partner = _base.Income.Partner, Caf = _base.Income.Caf }
            : new IncomeSettings { Net = t.Net, Partner = t.Partner, Caf = t.Caf };
    }

    public decimal AnnualBonusAt(PeriodKey p) => LastIncome(p)?.AnnualBonus ?? _base.AnnualBonus;

    private IncomeTerm? LastIncome(PeriodKey p)
    {
        IncomeTerm? last = null;
        foreach (var (from, t) in _income)
        {
            if (from > p) break;
            last = t;
        }
        return last;
    }
}
