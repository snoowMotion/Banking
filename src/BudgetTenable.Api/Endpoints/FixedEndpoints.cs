using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

/// <summary>
/// Suivi des charges fixes. Une charge est « payée » quand une opération est classée dedans : le clic en crée une
/// (saisie manuelle sur le compte de prélèvement de la charge), que le collage ou l'import rapprochera ensuite.
/// </summary>
public static class FixedEndpoints
{
    public static void MapFixed(this IEndpointRouteBuilder api)
    {
        api.MapPost("/periods/{key}/fixed/{categoryId}/paid", async (string key, string categoryId, FixedPaidInput? input,
            BudgetStore store, BudgetDbContext db, IClock clock, TimeProvider time, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var cat = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == categoryId, ct);
            if (cat is null) return Results.NotFound();
            var settings = await store.GetSettingsAsync(ct);
            if (cat.Group != CategoryGroup.Fixe) return BudgetEndpoints.Bad("Cette catégorie n'est pas une charge fixe.");
            if (cat.Id == BudgetCalculator.ArkeaId && settings.ArkeaSettled) return BudgetEndpoints.Bad("Cette charge est soldée.");

            var start = Periods.Start(k, settings.StartDay);
            var end = Periods.EndExclusive(k, settings.StartDay);
            if (await InPeriod(db, cat.Id, start, end).AnyAsync(ct))
                return Results.Conflict(new { error = "Cette charge est déjà payée sur cette période." });

            var amount = input?.Amount ?? cat.Amount;
            if (amount <= 0 || amount > 10_000_000m) return BudgetEndpoints.Bad("Montant invalide.");

            var accountId = input?.AccountId ?? cat.AccountId;
            var account = accountId is { } id
                ? await db.Accounts.FindAsync(new object[] { id }, ct)
                : await db.Accounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");

            // Par défaut : aujourd'hui si la période est en cours, sinon son premier jour (à venir) ou son dernier (passée).
            var today = clock.Today;
            var date = input?.Date ?? (today < start ? start : today >= end ? end.AddDays(-1) : today);
            if (date < start || date >= end) return BudgetEndpoints.Bad("La date est en dehors de la période.");

            var tx = new Transaction
            {
                AccountId = account.Id,
                BookingDate = date,
                Amount = -Math.Round(amount, 2),
                Currency = account.Currency,
                Note = "Marquée payée",
                CategoryId = cat.Id,
                CategorySource = CategorySource.Manual,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            db.Transactions.Add(tx);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/transactions/{tx.Id}", new { id = tx.Id });
        });

        // Annule un marquage : seulement si l'unique opération de la charge est une saisie manuelle.
        api.MapDelete("/periods/{key}/fixed/{categoryId}/paid", async (string key, string categoryId, BudgetStore store, BudgetDbContext db, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            var day = (await store.GetSettingsAsync(ct)).StartDay;
            var txs = await InPeriod(db, categoryId, Periods.Start(k, day), Periods.EndExclusive(k, day)).ToListAsync(ct);
            if (txs.Count == 0) return Results.NotFound();
            if (txs.Count > 1 || txs.Any(t => !t.IsManual))
                return Results.Conflict(new { error = "Cette charge vient de la banque ou compte plusieurs opérations : modifie-la dans le journal." });
            db.Transactions.Remove(txs[0]);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static IQueryable<Transaction> InPeriod(BudgetDbContext db, string categoryId, DateOnly start, DateOnly end) =>
        db.Transactions.Where(t => t.CategoryId == categoryId && !t.IsInternalTransfer && t.BookingDate >= start && t.BookingDate < end);
}
