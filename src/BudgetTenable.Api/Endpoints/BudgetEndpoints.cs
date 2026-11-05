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
        api.MapGet("/budget", async (BudgetStore store, IClock clock, CancellationToken ct) =>
        {
            var settings = await store.GetSettingsAsync(ct);
            var cats = await store.GetCategoriesAsync(ct);
            return Results.Ok(new BudgetDto(settings, cats.Select(CategoryDto.From).ToList(), clock.Today, Periods.KeyFor(clock.Today, settings.StartDay).ToString()));
        });

        api.MapPut("/budget", async (BudgetUpdate input, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var cats = await db.Categories.ToListAsync(ct);
            var s = input.Settings;

            if (!Periods.IsValidStartDay(s.StartDay)) return Bad("Le jour de début de période doit être compris entre 1 et 28.");
            if (!PeriodKey.TryParse(s.PotStart, out _)) return Bad("La première période de virement doit être au format AAAA-MM.");

            s.Income ??= new IncomeSettings();
            s.Income.Net = Money(s.Income.Net);
            s.Income.Partner = Money(s.Income.Partner);
            s.Income.Caf = Money(s.Income.Caf);
            s.AnnualBonus = Money(s.AnnualBonus);

            var accountIds = (await db.Accounts.Select(a => a.Id).ToListAsync(ct)).ToHashSet();
            var potIds = cats.Where(c => c.Group == CategoryGroup.Pot).Select(c => c.Id).ToHashSet();
            s.PotOpening = (s.PotOpening ?? new()).Where(kv => potIds.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => Math.Clamp(kv.Value, -10_000_000m, 10_000_000m));
            s.LddsDone = (s.LddsDone ?? new()).Where(kv => kv.Value && PeriodKey.TryParse(kv.Key, out _)).ToDictionary(kv => kv.Key, kv => true);

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
                if (u.Percentage is { } p && c.IsAllocation) c.Percentage = Math.Clamp(p, 0m, 100m);
                if (u.Amount is { } a && (c.Group == CategoryGroup.Fixe || c.Annual)) c.Amount = Math.Round(Math.Clamp(a, 0m, 10_000_000m), 2);
                if (c.Group == CategoryGroup.Fixe) c.AccountId = u.AccountId is { } acc && accountIds.Contains(acc) ? acc : null;
            }

            await store.SaveSettingsAsync(s, ct); // enregistre aussi les catégories modifiées (même contexte)
            var fresh = await store.GetCategoriesAsync(ct);
            return Results.Ok(new BudgetDto(s, fresh.Select(CategoryDto.From).ToList(), clock.Today, Periods.KeyFor(clock.Today, s.StartDay).ToString()));
        });

        // Comme « Réinitialiser » du prototype : valeurs de départ, mais on garde le réglage des pots et les virements LDDS cochés.
        api.MapPost("/budget/reset", async (BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            var current = await store.GetSettingsAsync(ct);
            var fresh = BudgetDefaults.Settings();
            fresh.PotStart = current.PotStart;
            fresh.PotOpening = current.PotOpening;
            fresh.LddsDone = current.LddsDone;

            var cats = await db.Categories.ToListAsync(ct);
            foreach (var d in BudgetDefaults.Categories())
            {
                var c = cats.FirstOrDefault(x => x.Id == d.Id);
                if (c is null) continue;
                c.Percentage = d.Percentage;
                c.Amount = d.Amount;
            }
            await store.SaveSettingsAsync(fresh, ct);
            var after = await store.GetCategoriesAsync(ct);
            return Results.Ok(new BudgetDto(fresh, after.Select(CategoryDto.From).ToList(), clock.Today, Periods.KeyFor(clock.Today, fresh.StartDay).ToString()));
        });

        api.MapGet("/periods/{key}", async (string key, BudgetStore store, BudgetDbContext db, IClock clock, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return Bad("Période invalide (AAAA-MM attendu).");
            var s = await store.GetSettingsAsync(ct);
            var cats = await store.GetCategoriesAsync(ct);
            var (from, to) = PeriodSummaryCalculator.RequiredWindow(s, k);

            var spendable = cats.Where(c => c.Group is CategoryGroup.Variable or CategoryGroup.Pot or CategoryGroup.Fixe).Select(c => c.Id).ToHashSet();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.CategoryId != null && !t.IsInternalTransfer && t.BookingDate >= from && t.BookingDate < to)
                .Select(t => new { t.Id, t.BookingDate, t.CategoryId, t.Amount, FromBank = t.ProviderTransactionId != null })
                .ToListAsync(ct);
            var entries = rows.Where(r => spendable.Contains(r.CategoryId!)).Select(r => new SpendEntry(r.BookingDate, r.CategoryId!, r.Amount, r.Id, r.FromBank)).ToList();

            return Results.Ok(PeriodSummaryCalculator.Calculate(s, cats, entries, k, clock.Today));
        });

        api.MapGet("/accounts", async (BudgetDbContext db, CancellationToken ct) =>
            await db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => new AccountDto(a.Id, a.Label, a.Type, a.Currency)).ToListAsync(ct));
    }

    private static decimal Money(decimal v) => Math.Round(Math.Clamp(v, 0m, 10_000_000m), 2);
    internal static IResult Bad(string message) => Results.BadRequest(new { error = message });
}
