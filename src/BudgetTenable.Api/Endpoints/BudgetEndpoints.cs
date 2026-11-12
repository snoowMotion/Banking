using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

public static class BudgetEndpoints
{
    public static void MapBudget(this IEndpointRouteBuilder api)
    {
        api.MapGet("/budget", async (string? period, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            PeriodKey? p = null;
            if (period is not null)
            {
                if (!PeriodKey.TryParse(period, out var k)) return Bad("Période invalide (AAAA-MM attendu).");
                p = k;
            }
            return Results.Ok(await Build(store, db, clock, p, ct));
        });

        // Modification du budget à partir d'une période : la période en cours par défaut. Les périodes d'avant ne bougent pas.
        api.MapPut("/budget", async (BudgetUpdate input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
            await Save(input, store, db, clock, ct));

        // Valeurs de départ à partir d'une période ; le réglage des pots, les virements cochés et le jour de début sont conservés.
        api.MapPost("/budget/reset", async (ResetRequest? req, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var current = await store.GetSettingsAsync(ct);
            var d = BudgetDefaults.Settings();
            d.IncludeBonus = current.IncludeBonus;
            d.StartDay = current.StartDay;
            d.PotStart = current.PotStart;
            d.PotOpening = current.PotOpening;
            d.LddsDone = current.LddsDone;
            var ids = (await db.Categories.Select(c => c.Id).ToListAsync(ct)).ToHashSet();
            var cats = BudgetDefaults.Categories().Where(c => ids.Contains(c.Id)).Select(c => new CategoryUpdate(c.Id, null, c.Percentage, c.Amount, null)).ToList();
            return await Save(new BudgetUpdate(d, cats, req?.Period), store, db, clock, ct);
        });

        api.MapGet("/periods/{key}", async (string key, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return Bad("Période invalide (AAAA-MM attendu).");
            var timeline = await store.LoadTimelineAsync(ct);
            var s = await store.GetSettingsAsync(ct);
            var (from, to) = PeriodSummaryCalculator.RequiredWindow(s, k);

            var spendable = timeline.AllCategories.Where(c => c.Group is CategoryGroup.Variable or CategoryGroup.Pot or CategoryGroup.Fixe).Select(c => c.Id).ToHashSet();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.CategoryId != null && !t.IsInternalTransfer && t.BookingDate >= from && t.BookingDate < to)
                .Select(t => new { t.Id, t.BookingDate, t.CategoryId, t.Amount, FromBank = t.ProviderTransactionId != null })
                .ToListAsync(ct);
            var entries = rows.Where(r => spendable.Contains(r.CategoryId!)).Select(r => new SpendEntry(r.BookingDate, r.CategoryId!, r.Amount, r.Id, r.FromBank)).ToList();

            return Results.Ok(PeriodSummaryCalculator.Calculate(timeline, entries, k, clock.Today));
        });

        api.MapGet("/accounts", async (BudgetDbContext db, CancellationToken ct) =>
            await db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => new AccountDto(a.Id, a.Label, a.Type, a.Currency)).ToListAsync(ct));
    }

    /// <summary>Le budget tel qu'il est à la période <paramref name="period"/> (la période en cours si absente).</summary>
    public static async Task<BudgetDto> Build(BudgetStore store, BudgetDbContext db, IClock clock, PeriodKey? period, CancellationToken ct)
    {
        var timeline = await store.LoadTimelineAsync(ct);
        var settings = await store.GetSettingsAsync(ct);
        var current = Periods.KeyFor(clock.Today, settings.StartDay);
        var p = period ?? current;

        var usage = await db.Transactions.AsNoTracking().Where(t => t.CategoryId != null)
            .GroupBy(t => t.CategoryId!).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        var nowStates = timeline.States(current).ToDictionary(x => x.Category.Id, x => x.Active);
        var cats = timeline.States(p)
            .OrderBy(x => x.Category.Order)
            .Select(x => CategoryDto.From(x, nowStates.GetValueOrDefault(x.Category.Id), usage.GetValueOrDefault(x.Category.Id)))
            .ToList();

        var view = timeline.At(p).Settings;
        var shown = new BudgetSettings
        {
            Income = view.Income, AnnualBonus = view.AnnualBonus, IncludeBonus = settings.IncludeBonus, StartDay = settings.StartDay,
            PotStart = settings.PotStart, PotOpening = settings.PotOpening, LddsDone = settings.LddsDone,
        };

        var later = new List<LaterChange>();
        foreach (var t in await db.CategoryTerms.AsNoTracking().ToListAsync(ct))
            if (PeriodKey.TryParse(t.From, out var f) && f > p) later.Add(new LaterChange("category", t.CategoryId, t.From));
        foreach (var t in await db.IncomeTerms.AsNoTracking().ToListAsync(ct))
            if (PeriodKey.TryParse(t.From, out var f) && f > p) later.Add(new LaterChange("income", null, t.From));
        later.Sort((a, b) => string.CompareOrdinal(a.From, b.From));

        return new BudgetDto(shown, cats, clock.Today, current.ToString(), p.ToString(), later);
    }

    private static async Task<IResult> Save(BudgetUpdate input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct)
    {
        var baseSettings = await store.GetSettingsAsync(ct);
        var s = input.Settings ?? new BudgetSettings();
        if (!Periods.IsValidStartDay(s.StartDay)) return Bad("Le jour de début de période doit être compris entre 1 et 28.");
        if (!PeriodKey.TryParse(s.PotStart, out _)) return Bad("La première période de virement doit être au format AAAA-MM.");

        PeriodKey p;
        if (input.Period is null) p = Periods.KeyFor(clock.Today, baseSettings.StartDay);
        else if (!PeriodKey.TryParse(input.Period, out p)) return Bad("Période invalide (AAAA-MM attendu).");
        var pKey = p.ToString();

        var cats = await db.Categories.ToListAsync(ct);
        var terms = await db.CategoryTerms.ToListAsync(ct);
        var incomeTerms = await db.IncomeTerms.ToListAsync(ct);
        var accountIds = (await db.Accounts.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var timeline = new BudgetTimeline(baseSettings, cats, terms, incomeTerms);
        // Les valeurs qui s'appliqueraient à la période sans le changement daté qu'on y pose : ce à quoi on compare.
        var without = new BudgetTimeline(baseSettings, cats, terms.Where(t => t.From != pKey), incomeTerms.Where(t => t.From != pKey));
        var propagate = input.Propagate == true;

        foreach (var u in input.Categories ?? new())
        {
            var c = cats.FirstOrDefault(x => x.Id == u.Id);
            if (c is null) return Bad($"Catégorie inconnue : {u.Id}.");
            if (u.Label is not null)
            {
                var label = u.Label.Trim();
                if (label.Length is 0 or > 120) return Bad("Le libellé d'une catégorie fait entre 1 et 120 caractères.");
                c.Label = label;
            }

            var state = timeline.StateOf(c, p);
            if (!state.Active) continue; // arrêtée ou pas encore créée à cette période : rien à régler

            var pct = state.Category.Percentage;
            var amt = state.Category.Amount;
            var acc = state.Category.AccountId;
            if (u.Percentage is { } x && c.IsAllocation) pct = Math.Clamp(x, 0m, 100m);
            if (u.Amount is { } a && (c.Group == CategoryGroup.Fixe || c.Annual)) amt = Math.Round(Math.Clamp(a, 0m, 10_000_000m), 2);
            if (c.Group == CategoryGroup.Fixe) acc = u.AccountId is { } id && accountIds.Contains(id) ? id : null;

            var prior = without.StateOf(c, p);
            var same = prior.Active && pct == prior.Category.Percentage && amt == prior.Category.Amount && acc == prior.Category.AccountId;
            var existing = terms.FirstOrDefault(t => t.CategoryId == c.Id && t.From == pKey);
            if (same)
            {
                if (existing is not null) db.CategoryTerms.Remove(existing);
            }
            else if (existing is not null)
            {
                existing.Percentage = pct; existing.Amount = amt; existing.AccountId = acc; existing.Active = true;
            }
            else db.CategoryTerms.Add(new CategoryTerm { CategoryId = c.Id, From = pKey, Percentage = pct, Amount = amt, AccountId = acc, Active = true });

            var changedHere = existing is not null || !same;
            if (propagate && changedHere)
                db.CategoryTerms.RemoveRange(terms.Where(t => t.CategoryId == c.Id && PeriodKey.TryParse(t.From, out var f) && f > p));
        }

        // Revenus et prime annuelle : même principe.
        var net = Money(s.Income?.Net ?? 0); var partner = Money(s.Income?.Partner ?? 0); var caf = Money(s.Income?.Caf ?? 0); var bonus = Money(s.AnnualBonus);
        var priorIncome = without.IncomeAt(p);
        var sameIncome = net == priorIncome.Net && partner == priorIncome.Partner && caf == priorIncome.Caf && bonus == without.AnnualBonusAt(p);
        var incomeTerm = incomeTerms.FirstOrDefault(t => t.From == pKey);
        if (sameIncome)
        {
            if (incomeTerm is not null) db.IncomeTerms.Remove(incomeTerm);
        }
        else if (incomeTerm is not null) { incomeTerm.Net = net; incomeTerm.Partner = partner; incomeTerm.Caf = caf; incomeTerm.AnnualBonus = bonus; }
        else db.IncomeTerms.Add(new IncomeTerm { From = pKey, Net = net, Partner = partner, Caf = caf, AnnualBonus = bonus });
        if (propagate && (incomeTerm is not null || !sameIncome))
            db.IncomeTerms.RemoveRange(incomeTerms.Where(t => PeriodKey.TryParse(t.From, out var f) && f > p));

        // Réglages communs à toutes les périodes (les revenus de départ restent ceux d'origine : l'historique les garde).
        var potIds = cats.Where(c => c.Group == CategoryGroup.Pot).Select(c => c.Id).ToHashSet();
        var global = new BudgetSettings
        {
            Income = baseSettings.Income,
            AnnualBonus = baseSettings.AnnualBonus,
            IncludeBonus = s.IncludeBonus,
            StartDay = s.StartDay,
            PotStart = s.PotStart,
            PotOpening = (s.PotOpening ?? new()).Where(kv => potIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, -10_000_000m, 10_000_000m)),
            LddsDone = (s.LddsDone ?? new()).Where(kv => kv.Value && PeriodKey.TryParse(kv.Key, out _)).ToDictionary(kv => kv.Key, kv => true),
        };
        await store.SaveSettingsAsync(global, ct); // enregistre aussi les changements ci-dessus (même contexte)
        return Results.Ok(await Build(store, db, clock, p, ct));
    }

    private static decimal Money(decimal v) => Math.Round(Math.Clamp(v, 0m, 10_000_000m), 2);
    internal static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
