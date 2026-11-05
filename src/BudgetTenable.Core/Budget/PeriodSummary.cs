using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Budget;

public sealed record EnvelopeLine(string CategoryId, string Label, decimal Budget, decimal Spent);
public sealed record PotLine(string CategoryId, string Label, decimal Monthly, decimal Balance, decimal SpentThisPeriod);
public sealed record HistoryPoint(string Key, string Label, int MonthIndex, decimal Spent);

/// <summary>Une charge fixe de la période : montant attendu face à ce qui a été réellement classé dedans.</summary>
public sealed record FixedLine(
    string CategoryId, string Label, decimal Expected, decimal Paid, int Count, DateOnly? LastDate,
    IReadOnlyList<int> TransactionIds,
    /// <summary>Vrai si toutes les opérations sont des saisies manuelles (donc annulables d'un clic).</summary>
    bool AllManual,
    int? AccountId,
    /// <summary>due (rien de classé), paid (montant conforme) ou gap (payée, mais montant différent).</summary>
    string Status);

public sealed record PeriodSummary(
    string Key,
    string Label,
    DateOnly Start,
    DateOnly End,
    int Length,
    /// <summary>Jours écoulés pris en compte : jusqu'à aujourd'hui (période en cours), tous (passée), zéro (à venir).</summary>
    int Elapsed,
    bool IsCurrent,
    bool IsPast,
    bool HasTransactions,
    decimal Income,
    decimal FixedMonthly,
    decimal Provisions,
    decimal VariableBudget,
    decimal VariableSpent,
    decimal Rest,
    int DaysLeft,
    decimal? PerDay,
    decimal? Projection,
    decimal Balance,
    decimal SavingsTarget,
    IReadOnlyList<EnvelopeLine> Envelopes,
    IReadOnlyList<PotLine> Pots,
    decimal PotsTotal,
    /// <summary>Dépenses variables cumulées, indice i = fin du jour i (0 = début), jusqu'à <see cref="Elapsed"/>.</summary>
    IReadOnlyList<decimal> DailyCumulative,
    IReadOnlyList<HistoryPoint> History,
    bool LddsDone,
    IReadOnlyList<FixedLine> FixedLines);

/// <summary>
/// Dépense imputée à une catégorie : montant signé comme en banque (négatif = débit).
/// Un crédit rattaché à une catégorie réduit donc la dépense de cette catégorie.
/// </summary>
public readonly record struct SpendEntry(DateOnly Date, string CategoryId, decimal Amount, int Id = 0, bool FromBank = false)
{
    public decimal Spent => -Amount;
}

public static class PeriodSummaryCalculator
{
    public const int HistoryLength = 6;

    /// <summary>Fenêtre de dates dont <see cref="Calculate"/> a besoin pour une période donnée.</summary>
    public static (DateOnly From, DateOnly To) RequiredWindow(BudgetSettings s, PeriodKey key)
    {
        var from = Periods.Start(key.AddMonths(-(HistoryLength - 1)), s.StartDay);
        if (PeriodKey.TryParse(s.PotStart, out var potStart))
        {
            var ps = Periods.Start(potStart, s.StartDay);
            if (ps < from) from = ps;
        }
        return (from, Periods.EndExclusive(key, s.StartDay));
    }

    /// <param name="entries">Transactions catégorisées, hors virements internes, dans la fenêtre requise.</param>
    public static PeriodSummary Calculate(
        BudgetSettings s,
        IReadOnlyCollection<Category> categories,
        IReadOnlyCollection<SpendEntry> entries,
        PeriodKey key,
        DateOnly today)
    {
        var r = BudgetCalculator.Compute(s, categories);
        var day = s.StartDay;
        var start = Periods.Start(key, day);
        var endEx = Periods.EndExclusive(key, day);
        var length = Periods.Length(key, day);

        var current = Periods.KeyFor(today, day);
        var isCurrent = key == current;
        var isPast = key < current;
        var elapsed = isCurrent ? Periods.Elapsed(key, day, today) : (isPast ? length : 0);

        bool InPeriod(SpendEntry e) => e.Date >= start && e.Date < endEx;
        var inPeriodAll = entries.Where(InPeriod).ToList();
        var fixedIds = categories.Where(c => c.Group == CategoryGroup.Fixe).Select(c => c.Id).ToHashSet();
        var inPeriod = inPeriodAll.Where(e => !fixedIds.Contains(e.CategoryId)).ToList();
        var spentBy = inPeriod.GroupBy(e => e.CategoryId).ToDictionary(g => g.Key, g => g.Sum(e => e.Spent));
        decimal SpentIn(string id) => spentBy.GetValueOrDefault(id);

        var variableCats = categories.Where(c => c is { IsAllocation: true, Group: CategoryGroup.Variable }).OrderBy(c => c.Order).ToList();
        var potCats = categories.Where(c => c.Group == CategoryGroup.Pot).OrderBy(c => c.Annual).ThenBy(c => c.Order).ToList();
        var variableIds = variableCats.Select(c => c.Id).ToHashSet();

        var envelopes = variableCats
            .Select(c => new EnvelopeLine(c.Id, c.Label, Math.Max(0, r.Amounts[c.Id]), SpentIn(c.Id)))
            .ToList();
        var varSpent = variableCats.Sum(c => SpentIn(c.Id));
        var varBudget = r.VariableTotal;
        var rest = varBudget - varSpent;

        var daysLeft = isCurrent ? length - elapsed + 1 : 0;
        decimal? perDay = daysLeft > 0 ? Math.Max(0, rest) / daysLeft : null;
        decimal? projection = elapsed > 0 ? varSpent / elapsed * length : null;

        var potStartOk = PeriodKey.TryParse(s.PotStart, out var potStart);
        var pots = potCats.Select(c =>
        {
            var monthly = BudgetCalculator.MonthlyAmount(c, r);
            decimal balance;
            if (potStartOk)
            {
                var n = Math.Max(0, PeriodKey.MonthsBetween(potStart, key) + 1);
                var spent = entries
                    .Where(e => e.CategoryId == c.Id)
                    .Where(e => { var k = Periods.KeyFor(e.Date, day); return k >= potStart && k <= key; })
                    .Sum(e => e.Spent);
                balance = s.PotOpening.GetValueOrDefault(c.Id) + n * monthly - spent;
            }
            else balance = s.PotOpening.GetValueOrDefault(c.Id);
            return new PotLine(c.Id, c.Label, monthly, balance, SpentIn(c.Id));
        }).ToList();

        var daily = new decimal[Math.Max(0, Math.Min(elapsed, length)) + 1];
        if (daily.Length > 1)
        {
            var perDayVar = inPeriod
                .Where(e => variableIds.Contains(e.CategoryId))
                .GroupBy(e => e.Date.DayNumber - start.DayNumber)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.Spent));
            for (var i = 1; i < daily.Length; i++)
                daily[i] = daily[i - 1] + perDayVar.GetValueOrDefault(i - 1);
        }

        var history = new List<HistoryPoint>();
        for (var i = HistoryLength - 1; i >= 0; i--)
        {
            var k = key.AddMonths(-i);
            var a = Periods.Start(k, day);
            var b = Periods.EndExclusive(k, day);
            var v = entries.Where(e => e.Date >= a && e.Date < b && variableIds.Contains(e.CategoryId)).Sum(e => e.Spent);
            history.Add(new HistoryPoint(k.ToString(), Periods.Label(k, day), k.Month, v));
        }

        var fixedLines = categories
            .Where(c => c.Group == CategoryGroup.Fixe && c.Amount > 0 && !(c.Id == BudgetCalculator.ArkeaId && s.ArkeaSettled))
            .OrderBy(c => c.Order)
            .Select(c =>
            {
                var es = inPeriodAll.Where(e => e.CategoryId == c.Id).OrderBy(e => e.Date).ToList();
                var paid = es.Sum(e => e.Spent);
                var status = es.Count == 0 ? "due" : Math.Abs(paid - c.Amount) <= 0.5m ? "paid" : "gap";
                return new FixedLine(c.Id, c.Label, c.Amount, paid, es.Count, es.Count > 0 ? es[^1].Date : null,
                    es.Select(e => e.Id).ToList(), es.All(e => !e.FromBank), c.AccountId, status);
            }).ToList();

        var provisions = r.LddsTransfer;
        return new PeriodSummary(
            key.ToString(), Periods.Label(key, day), start, endEx.AddDays(-1), length, elapsed, isCurrent, isPast,
            inPeriod.Count > 0, r.Income, r.FixedMonthly, provisions, varBudget, varSpent, rest, daysLeft, perDay, projection,
            r.Income - r.FixedMonthly - provisions - varSpent, r.SavingsAmount, envelopes, pots, pots.Sum(p => p.Balance),
            daily, history, s.LddsDone.GetValueOrDefault(key.ToString()), fixedLines);
    }
}
