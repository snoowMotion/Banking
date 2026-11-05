using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Import;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

public static class PasteEndpoints
{
    private const int MaxChars = 200_000;
    private const int MaxItems = 500;

    public static void MapPaste(this IEndpointRouteBuilder api)
    {
        api.MapPost("/paste/preview", Preview);
        api.MapPost("/paste/commit", Commit);

        api.MapPost("/categories", async (CategoryCreate input, BudgetDbContext db, CancellationToken ct) =>
        {
            if (CategoryCreator.Validate(input.Label) is { } err) return BudgetEndpoints.Bad(err);
            var all = await db.Categories.ToListAsync(ct);
            if (CategoryCreator.FindByLabel(all, input.Label!) is not null) return Results.Conflict(new { error = "Une catégorie porte déjà ce nom." });
            var c = CategoryCreator.Create(db, all, input.Label!, input.Group ?? CategoryGroup.Variable);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/categories/{c.Id}", CategoryDto.From(c));
        });

        api.MapGet("/bank-categories", async (BudgetDbContext db, CancellationToken ct) =>
            (await db.BankCategoryMappings.AsNoTracking().OrderBy(m => m.Label).ToListAsync(ct)).Select(m => new BankCategoryDto(m.Key, m.Label, m.CategoryId)).ToList());

        api.MapDelete("/bank-categories", async (string key, BudgetDbContext db, CancellationToken ct) =>
        {
            var m = await db.BankCategoryMappings.FindAsync(new object[] { key }, ct);
            if (m is null) return Results.NotFound();
            db.BankCategoryMappings.Remove(m);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static async Task<IResult> Preview(PastePreviewRequest req, BudgetDbContext db, BudgetStore store, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Text)) return BudgetEndpoints.Bad("Colle d'abord les opérations copiées depuis ta banque.");
        if (req.Text.Length > MaxChars) return BudgetEndpoints.Bad("Texte trop long.");
        var account = req.AccountId is { } id ? await db.Accounts.FindAsync(new object[] { id }, ct) : await db.Accounts.OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
        if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");

        var parsed = PastedStatementParser.Parse(req.Text, clock.Today);
        if (parsed.Items.Count == 0)
            return BudgetEndpoints.Bad("Aucune opération reconnue. Colle le texte tel que copié sur le site de ta banque : libellé, date puis montant pour chaque opération.");
        if (parsed.Items.Count > MaxItems) return BudgetEndpoints.Bad($"Trop d'opérations d'un coup ({MaxItems} au plus).");

        var min = parsed.Items.Min(i => i.Date).AddDays(-Reconciler.WindowDays);
        var max = parsed.Items.Max(i => i.Date).AddDays(Reconciler.WindowDays);
        var existing = await db.Transactions.AsNoTracking().Where(t => t.AccountId == account.Id && t.BookingDate >= min && t.BookingDate <= max).ToListAsync(ct);
        var matches = Reconciler.Match(parsed.Items.Select(i => (i, i.ProviderId!)).ToList(), existing.Select(Reconciler.ToCandidate).ToList());

        var rules = RuleMatcher.Ordered(await db.Rules.AsNoTracking().ToListAsync(ct));
        var mappings = await db.BankCategoryMappings.AsNoTracking().ToDictionaryAsync(m => m.Key, ct);
        var categories = await db.Categories.AsNoTracking().ToListAsync(ct);

        // Charges fixes pas encore payées dans la période de chaque opération : un montant identique à l'une d'elles est une piste.
        var settings = await store.GetSettingsAsync(ct);
        var day = settings.StartDay;
        var fixedCats = categories.Where(c => c.Group == CategoryGroup.Fixe && c.Amount > 0 && !(c.Id == BudgetCalculator.ArkeaId && settings.ArkeaSettled)).ToList();
        var paidFixed = new HashSet<(string, string)>();
        if (fixedCats.Count > 0)
        {
            var from = Periods.Start(Periods.KeyFor(parsed.Items.Min(x => x.Date), day), day);
            var to = Periods.EndExclusive(Periods.KeyFor(parsed.Items.Max(x => x.Date), day), day);
            var fixedIds = fixedCats.Select(c => c.Id).ToHashSet();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.CategoryId != null && !t.IsInternalTransfer && t.BookingDate >= from && t.BookingDate < to)
                .Select(t => new { t.CategoryId, t.BookingDate }).ToListAsync(ct);
            foreach (var r in rows.Where(r => fixedIds.Contains(r.CategoryId!)))
                paidFixed.Add((r.CategoryId!, Periods.KeyFor(r.BookingDate, day).ToString()));
        }

        var items = new List<PasteItem>();
        for (var i = 0; i < parsed.Items.Count; i++)
        {
            var p = parsed.Items[i];
            var m = matches[i];
            var ex = m.ExistingId is { } eid ? existing.First(t => t.Id == eid) : null;

            // Priorité : règles de l'utilisateur, puis catégorie déjà posée sur l'opération rapprochée, puis correspondance bancaire retenue.
            string? categoryId = null, source = "none";
            var internalTransfer = false;
            var probe = new Transaction { Counterparty = p.Counterparty, RemittanceInfo = p.Remittance, Amount = p.Amount };
            if (RuleMatcher.Apply(probe, rules)) { categoryId = probe.CategoryId; internalTransfer = probe.IsInternalTransfer; source = "rule"; }
            else if (ex is { CategoryId: not null }) { categoryId = ex.CategoryId; source = "existing"; }
            else if (p.BankCategory is not null && mappings.TryGetValue(CategoryCreator.KeyOf(p.BankCategory), out var map) && map.CategoryId is not null)
            { categoryId = map.CategoryId; source = "mapping"; }
            else if (p.Amount < 0)
            {
                // Piste, jamais imposée : même montant exact qu'UNE seule charge fixe encore à payer, et soit un libellé qui lui
                // ressemble, soit un prélèvement (un montant rond seul donnerait trop de fausses pistes).
                var period = Periods.KeyFor(p.Date, day).ToString();
                var text = $"{p.Counterparty} {p.Remittance}";
                var debit = TextNormalizer.Normalize(p.PaymentMethod).StartsWith("PRELEVEMENT", StringComparison.Ordinal);
                var hits = fixedCats
                    .Where(c => c.Amount == -p.Amount && !paidFixed.Contains((c.Id, period)) && (debit || Reconciler.TextOverlap(text, $"{c.Label} {c.Note}")))
                    .ToList();
                if (hits.Count == 1) { categoryId = hits[0].Id; source = "fixed"; paidFixed.Add((hits[0].Id, period)); }
            }

            items.Add(new PasteItem(i, p.Date, p.Time?.ToString("HH:mm"), p.Amount, p.Counterparty ?? "", p.Remittance, p.BankCategory, p.PaymentMethod, p.Pending,
                p.ProviderId!, m.Kind.ToString().ToLowerInvariant(), m.Ambiguous,
                ex is null ? null : new ExistingRef(ex.Id, ex.BookingDate, ex.Note ?? ex.Counterparty ?? ex.RemittanceInfo ?? "", ex.CategoryId, ex.IsManual, ex.Status == TransactionStatus.Pending),
                categoryId, source, internalTransfer, p.BankCategory is null ? null : CategoryCreator.KeyOf(p.BankCategory)));
        }

        // Catégories bancaires rencontrées, avec ce qu'on en sait déjà.
        var bank = items.Where(i => i.BankCategory is not null)
            .GroupBy(i => CategoryCreator.KeyOf(i.BankCategory!))
            .Select(g =>
            {
                var label = g.First().BankCategory!;
                mappings.TryGetValue(g.Key, out var map);
                return new BankCategoryInfo(g.Key, label, g.Count(), map is not null, map is not null && map.CategoryId is null, map?.CategoryId,
                    CategoryCreator.FindByLabel(categories, label)?.Id);
            }).ToList();

        return Results.Ok(new PastePreview(account.Id, items, bank, parsed.Ignored));
    }

    private static async Task<IResult> Commit(PasteCommitRequest req, BudgetDbContext db, TimeProvider time, CancellationToken ct)
    {
        var inItems = req.Items ?? new();
        if (inItems.Count == 0) return BudgetEndpoints.Bad("Rien à ajouter.");
        if (inItems.Count > MaxItems) return BudgetEndpoints.Bad($"Trop d'opérations d'un coup ({MaxItems} au plus).");
        var account = await db.Accounts.FindAsync(new object[] { req.AccountId }, ct);
        if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");

        await using var dbTx = await db.Database.BeginTransactionAsync(ct);

        // 1. Nouvelles catégories (un libellé déjà pris est réutilisé plutôt que dupliqué).
        var categories = await db.Categories.ToListAsync(ct);
        var created = 0;
        var newIds = new Dictionary<string, string>();
        foreach (var n in req.NewCategories ?? new())
        {
            if (string.IsNullOrWhiteSpace(n.Key)) return BudgetEndpoints.Bad("Catégorie à créer sans clé.");
            if (CategoryCreator.Validate(n.Label) is { } err) return BudgetEndpoints.Bad(err);
            var found = CategoryCreator.FindByLabel(categories, n.Label);
            if (found is null) { found = CategoryCreator.Create(db, categories, n.Label, n.Group); created++; }
            newIds[n.Key] = found.Id;
        }
        var knownIds = categories.Select(c => c.Id).ToHashSet();
        string? Resolve(string? categoryId, string? newKey, out bool bad)
        {
            bad = false;
            if (newKey is not null) { if (newIds.TryGetValue(newKey, out var nid)) return nid; bad = true; return null; }
            if (categoryId is null) return null;
            if (knownIds.Contains(categoryId)) return categoryId;
            bad = true;
            return null;
        }

        // 2. Correspondances retenues pour les prochains collages.
        var mappingsSaved = 0;
        foreach (var cm in req.Mappings ?? new())
        {
            if (string.IsNullOrWhiteSpace(cm.BankCategory) || cm.BankCategory.Length > 120) continue;
            var badMap = false;
            var target = cm.Ignore ? null : Resolve(cm.CategoryId, cm.NewCategoryKey, out badMap);
            if (!cm.Ignore && (target is null || badMap)) continue;
            var key = CategoryCreator.KeyOf(cm.BankCategory);
            var row = await db.BankCategoryMappings.FindAsync(new object[] { key }, ct);
            if (row is null) db.BankCategoryMappings.Add(new BankCategoryMapping { Key = key, Label = cm.BankCategory.Trim(), CategoryId = target });
            else row.CategoryId = target;
            mappingsSaved++;
        }

        // 3. Opérations.
        var rules = RuleMatcher.Ordered(await db.Rules.AsNoTracking().ToListAsync(ct));
        var existingIds = (await db.Transactions.Where(t => t.AccountId == account.Id && t.ProviderTransactionId != null).Select(t => t.ProviderTransactionId!).ToListAsync(ct)).ToHashSet();
        var now = time.GetUtcNow().UtcDateTime;
        int added = 0, reconciled = 0, skipped = 0;
        var merged = new HashSet<int>();

        foreach (var it in inItems)
        {
            var action = (it.Action ?? "add").ToLowerInvariant();
            if (action == "skip") { skipped++; continue; }
            if (it.Amount == 0 || Math.Abs(it.Amount) > 10_000_000m || string.IsNullOrWhiteSpace(it.ProviderId) || it.ProviderId.Length > 200) { skipped++; continue; }
            var chosen = Resolve(it.CategoryId, it.NewCategoryKey, out var bad);
            if (bad) return BudgetEndpoints.Bad("Catégorie inconnue.");
            var label = Truncate(AmountDate.Clean(it.Label), 300);
            var detail = Truncate(AmountDate.Clean(it.Detail), 600);
            var parsed = new ParsedTransaction(it.Date, null, Math.Round(it.Amount, 2), label, detail, it.ProviderId, null, it.Pending);

            if (action == "reconcile" && it.ReconcileId is { } rid && !merged.Contains(rid))
            {
                var target = await db.Transactions.FirstOrDefaultAsync(t => t.Id == rid, ct);
                var idTaken = existingIds.Contains(it.ProviderId) && target?.ProviderTransactionId != it.ProviderId;
                if (target is null || target.AccountId != account.Id || target.Amount != parsed.Amount || idTaken) { skipped++; continue; }
                Reconciler.Merge(target, parsed, it.ProviderId);
                if (chosen is not null && target.CategorySource == CategorySource.None && !it.Internal) { target.CategoryId = chosen; target.CategorySource = CategorySource.Manual; }
                existingIds.Add(it.ProviderId);
                merged.Add(rid);
                reconciled++;
                continue;
            }

            if (!existingIds.Add(it.ProviderId)) { skipped++; continue; }
            var tx = new Transaction
            {
                AccountId = account.Id, ProviderTransactionId = it.ProviderId, BookingDate = it.Date, Amount = parsed.Amount, Currency = account.Currency,
                Status = it.Pending ? TransactionStatus.Pending : TransactionStatus.Booked, Counterparty = label, RemittanceInfo = detail, CreatedAt = now,
            };
            if (it.Internal) { tx.IsInternalTransfer = true; tx.CategorySource = CategorySource.Manual; }
            else
            {
                var byRule = RuleMatcher.Apply(tx, rules);
                // Un choix explicite différent de la règle l'emporte ; la règle seule reste une règle.
                if (chosen is not null && (!byRule || chosen != tx.CategoryId)) { tx.CategoryId = chosen; tx.IsInternalTransfer = false; tx.CategorySource = CategorySource.Manual; }
            }
            db.Transactions.Add(tx);
            added++;
        }

        await db.SaveChangesAsync(ct);
        await dbTx.CommitAsync(ct);
        var toSort = await db.Transactions.CountAsync(t => t.CategoryId == null && !t.IsInternalTransfer, ct);
        return Results.Ok(new PasteCommitResult(added, reconciled, skipped, created, mappingsSaved, toSort));
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
