using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

/// <summary>
/// Primes et rentrées exceptionnelles : une saisie crée une opération positive classée dans la ligne dédiée (groupe
/// <see cref="CategoryGroup.Extra"/>). Elle s'ajoute au « Reste du mois » de sa période ; la synchro, l'import ou le collage
/// la rapprochent ensuite de la ligne bancaire réelle (même montant, même compte, à 5 jours près), comme une charge fixe cliquée.
/// La suppression d'une saisie passe par <c>DELETE /api/transactions/{id}</c>.
/// </summary>
public static class ExtraEndpoints
{
    public static void MapExtras(this IEndpointRouteBuilder api)
    {
        api.MapPost("/periods/{key}/extras", async (string key, ExtraInput? input,
            BudgetStore store, BudgetDbContext db, IClock clock, TimeProvider time, CancellationToken ct) =>
        {
            if (!PeriodKey.TryParse(key, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
            if (input?.Amount is not { } raw || raw <= 0 || raw > 10_000_000m) return BudgetEndpoints.Bad("Saisis un montant positif.");

            var cat = await db.Categories.AsNoTracking().OrderBy(c => c.Order).FirstOrDefaultAsync(c => c.Group == CategoryGroup.Extra, ct);
            if (cat is null) return BudgetEndpoints.Bad("La ligne des primes est introuvable.");

            var settings = await store.GetSettingsAsync(ct);
            var start = Periods.Start(k, settings.StartDay);
            var end = Periods.EndExclusive(k, settings.StartDay);

            var account = input.AccountId is { } id
                ? await db.Accounts.FindAsync(new object[] { id }, ct)
                : await db.Accounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");

            // Par défaut : aujourd'hui si la période est en cours, sinon son premier jour (à venir) ou son dernier (passée).
            var today = clock.Today;
            var date = input.Date ?? (today < start ? start : today >= end ? end.AddDays(-1) : today);
            if (date < start || date >= end) return BudgetEndpoints.Bad("La date est en dehors de la période.");

            var label = input.Label?.Trim();
            if (string.IsNullOrEmpty(label)) label = "Prime";
            if (label.Length > 120) label = label[..120];

            var tx = new Transaction
            {
                AccountId = account.Id,
                BookingDate = date,
                Amount = Math.Round(raw, 2),
                Currency = account.Currency,
                Note = label,
                CategoryId = cat.Id,
                CategorySource = CategorySource.Manual,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            db.Transactions.Add(tx);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/transactions/{tx.Id}", new { id = tx.Id });
        });
    }
}
