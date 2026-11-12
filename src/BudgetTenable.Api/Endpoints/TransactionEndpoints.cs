using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

public static class TransactionEndpoints
{
    public static void MapTransactions(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/transactions");

        g.MapGet("", async (string? period, string? category, string? q, bool? unsorted, int? limit, int? offset,
            BudgetStore store, BudgetDbContext db, CancellationToken ct) =>
        {
            IQueryable<Transaction> query = db.Transactions.AsNoTracking().Include(t => t.Account);

            if (unsorted == true) query = query.Where(t => t.CategoryId == null && !t.IsInternalTransfer);
            if (!string.IsNullOrEmpty(period))
            {
                if (!PeriodKey.TryParse(period, out var k)) return BudgetEndpoints.Bad("Période invalide (AAAA-MM attendu).");
                var day = (await store.GetSettingsAsync(ct)).StartDay;
                var from = Periods.Start(k, day);
                var to = Periods.EndExclusive(k, day);
                query = query.Where(t => t.BookingDate >= from && t.BookingDate < to);
            }
            if (!string.IsNullOrEmpty(category)) query = query.Where(t => t.CategoryId == category);

            var rows = await query.OrderByDescending(t => t.BookingDate).ThenByDescending(t => t.Id).ToListAsync(ct);

            if (!string.IsNullOrWhiteSpace(q))
            {
                var needle = TextNormalizer.Normalize(q);
                var labels = (await store.GetCategoriesAsync(ct)).ToDictionary(c => c.Id, c => c.Label);
                rows = rows.Where(t => TextNormalizer.Normalize(string.Join(' ',
                    t.Note, t.Counterparty, t.RemittanceInfo, t.CategoryId is null ? "" : labels.GetValueOrDefault(t.CategoryId, ""))).Contains(needle)).ToList();
            }

            var take = Math.Clamp(limit ?? 500, 1, 2000);
            var items = rows.Skip(Math.Max(0, offset ?? 0)).Take(take).Select(TransactionDto.From).ToList();
            return Results.Ok(new TransactionList(rows.Count, items));
        });

        g.MapGet("/unsorted-count", async (BudgetDbContext db, CancellationToken ct) =>
            Results.Ok(new { count = await db.Transactions.CountAsync(t => t.CategoryId == null && !t.IsInternalTransfer, ct) }));

        g.MapPost("", async (TransactionInput input, BudgetDbContext db, IClock clock, TimeProvider time, CancellationToken ct) =>
        {
            if (input.Amount is null or 0m) return BudgetEndpoints.Bad("Saisis un montant.");
            if (Math.Abs(input.Amount.Value) > 10_000_000m) return BudgetEndpoints.Bad("Montant trop élevé.");
            var account = input.AccountId is { } id
                ? await db.Accounts.FindAsync(new object[] { id }, ct)
                : await db.Accounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
            if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");
            if (input.CategoryId is not null && !await db.Categories.AnyAsync(c => c.Id == input.CategoryId, ct)) return BudgetEndpoints.Bad("Catégorie inconnue.");

            var tx = new Transaction
            {
                AccountId = account.Id,
                BookingDate = input.Date ?? clock.Today,
                Amount = Math.Round(input.Amount.Value, 2),
                Currency = account.Currency,
                Note = Clean(input.Note),
                CategoryId = input.IsInternalTransfer == true ? null : input.CategoryId,
                IsInternalTransfer = input.IsInternalTransfer == true,
                CreatedAt = time.GetUtcNow().UtcDateTime,
            };
            tx.CategorySource = tx.CategoryId is null && !tx.IsInternalTransfer ? CategorySource.None : CategorySource.Manual;
            db.Transactions.Add(tx);
            await db.SaveChangesAsync(ct);
            tx.Account = account;
            return Results.Created($"/api/transactions/{tx.Id}", TransactionDto.From(tx));
        });

        g.MapPut("/{id:int}", async (int id, TransactionInput input, BudgetDbContext db, CancellationToken ct) =>
        {
            var tx = await db.Transactions.Include(t => t.Account).FirstOrDefaultAsync(t => t.Id == id, ct);
            if (tx is null) return Results.NotFound();
            if (input.CategoryId is not null && !await db.Categories.AnyAsync(c => c.Id == input.CategoryId, ct)) return BudgetEndpoints.Bad("Catégorie inconnue.");

            // Une opération venue de la banque garde sa date, son montant et son compte : seul le classement et la note se modifient.
            if (tx.IsManual)
            {
                if (input.Amount is null or 0m || Math.Abs(input.Amount.Value) > 10_000_000m) return BudgetEndpoints.Bad("Saisis un montant valide.");
                tx.Amount = Math.Round(input.Amount.Value, 2);
                if (input.Date is { } d) tx.BookingDate = d;
                if (input.AccountId is { } aid && aid != tx.AccountId)
                {
                    var acc = await db.Accounts.FindAsync(new object[] { aid }, ct);
                    if (acc is null) return BudgetEndpoints.Bad("Compte inconnu.");
                    tx.AccountId = acc.Id;
                    tx.Account = acc;
                }
            }
            tx.Note = Clean(input.Note);
            tx.IsInternalTransfer = input.IsInternalTransfer == true;
            tx.CategoryId = tx.IsInternalTransfer ? null : input.CategoryId;
            tx.CategorySource = tx.CategoryId is null && !tx.IsInternalTransfer ? CategorySource.None : CategorySource.Manual;
            await db.SaveChangesAsync(ct);
            return Results.Ok(TransactionDto.From(tx));
        });

        g.MapDelete("/{id:int}", async (int id, BudgetDbContext db, CancellationToken ct) =>
        {
            var tx = await db.Transactions.FindAsync(new object[] { id }, ct);
            if (tx is null) return Results.NotFound();
            db.Transactions.Remove(tx);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static string? Clean(string? s)
    {
        var t = s?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > 120 ? t[..120] : t;
    }
}
