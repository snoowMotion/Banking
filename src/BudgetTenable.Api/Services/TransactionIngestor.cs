using BudgetTenable.Api.Data;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Import;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Services;

/// <param name="Seen">Opérations déjà en base retrouvées dans le lot (doublons et rapprochements).</param>
/// <param name="Window">Opérations du compte chargées pour le rapprochement (suivies par EF).</param>
public sealed record IngestOutcome(int Added, int Duplicates, int Categorized, int Reconciled, HashSet<int> Seen, List<Transaction> Window);

/// <summary>
/// Fait entrer un lot d'opérations sans aperçu (fichier CSV/OFX, synchro bancaire) : dédoublonnage,
/// rapprochement avec les saisies manuelles et les opérations en attente, puis règles de classement.
/// N'enregistre pas : l'appelant appelle <c>SaveChangesAsync</c>.
/// </summary>
public static class TransactionIngestor
{
    /// <param name="bankSync">
    /// Synchro bancaire : une opération collée ou importée à quelques jours près est reprise plutôt que doublée,
    /// et un rapprochement entre plusieurs candidats équivalents est fait quand même (personne n'est là pour trancher,
    /// et le nombre d'opérations reste juste). Pour un fichier, un doute ajoute l'opération plutôt que d'en écraser une.
    /// </param>
    public static async Task<IngestOutcome> IngestAsync(
        BudgetDbContext db, Account account, IReadOnlyList<(ParsedTransaction Tx, string Id)> assigned,
        DateTime nowUtc, bool bankSync, CancellationToken ct)
    {
        var seen = new HashSet<int>();
        if (assigned.Count == 0) return new IngestOutcome(0, 0, 0, 0, seen, new List<Transaction>());

        var min = assigned.Min(r => r.Tx.Date).AddDays(-Reconciler.WindowDays);
        var max = assigned.Max(r => r.Tx.Date).AddDays(Reconciler.WindowDays);
        // Suivies par EF : une saisie manuelle ou une opération en attente rapprochée est mise à jour sur place.
        var existing = await db.Transactions
            .Where(t => t.AccountId == account.Id && t.BookingDate >= min && t.BookingDate <= max)
            .ToListAsync(ct);
        var matches = Reconciler.Match(assigned, existing.Select(t => Reconciler.ToCandidate(t, bankSync)).ToList());

        var rules = RuleMatcher.Ordered(await db.Rules.AsNoTracking().ToListAsync(ct));
        var knownIds = existing.Where(t => t.ProviderTransactionId != null).Select(t => t.ProviderTransactionId!).ToHashSet();
        int added = 0, duplicates = 0, categorized = 0, reconciled = 0;
        for (var i = 0; i < assigned.Count; i++)
        {
            var (row, id) = assigned[i];
            var m = matches[i];
            if (m.Kind == MatchKind.Duplicate)
            {
                if (m.ExistingId is { } dup) seen.Add(dup);
                duplicates++;
                continue;
            }
            if (m.Kind == MatchKind.Reconcile && (bankSync || !m.Ambiguous))
            {
                var target = existing.First(t => t.Id == m.ExistingId);
                // L'identifiant d'arrivée peut déjà être pris (opération connue hors de la fenêtre de dates) : on ne fusionne pas.
                if (target.ProviderTransactionId == id || knownIds.Add(id))
                {
                    Reconciler.Merge(target, row, id);
                    seen.Add(target.Id);
                    reconciled++;
                    continue;
                }
                duplicates++;
                continue;
            }
            if (!knownIds.Add(id)) { duplicates++; continue; }
            var tx = new Transaction
            {
                AccountId = account.Id,
                ProviderTransactionId = id,
                BookingDate = row.Date,
                ValueDate = row.ValueDate,
                Amount = row.Amount,
                Currency = account.Currency,
                Status = row.Pending ? TransactionStatus.Pending : TransactionStatus.Booked,
                Counterparty = Truncate(row.Counterparty, 300),
                RemittanceInfo = Truncate(row.Remittance, 600),
                CreatedAt = nowUtc,
            };
            if (RuleMatcher.Apply(tx, rules)) categorized++;
            db.Transactions.Add(tx);
            added++;
        }
        return new IngestOutcome(added, duplicates, categorized, reconciled, seen, existing);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
