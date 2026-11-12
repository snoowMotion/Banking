using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;

namespace BudgetTenable.Core.Import;

public enum MatchKind
{
    /// <summary>Aucune opération connue ne correspond : à ajouter.</summary>
    New,
    /// <summary>Déjà dans la base, rien à faire.</summary>
    Duplicate,
    /// <summary>Correspond à une saisie manuelle ou à une opération en attente : à fusionner.</summary>
    Reconcile,
}

/// <param name="Adoptable">
/// Opération débitée venue d'un collage ou d'un fichier, que la synchro bancaire peut reprendre à son compte :
/// la date affichée par le site de la banque et la date comptable de l'API diffèrent souvent d'un jour ou deux.
/// </param>
public sealed record ExistingCandidate(int Id, DateOnly Date, decimal Amount, bool IsManual, bool IsPending, string? ProviderId, string Text, bool Adoptable = false);
public sealed record MatchResult(MatchKind Kind, int? ExistingId, bool Ambiguous);

/// <summary>
/// Rapproche des opérations entrantes (collage, CSV, OFX) de celles déjà en base.
/// Même compte et même montant exact sont toujours exigés.
/// </summary>
public static class Reconciler
{
    public const int WindowDays = 5;

    public static IReadOnlyList<MatchResult> Match(IReadOnlyList<(ParsedTransaction Tx, string Id)> incoming, IReadOnlyList<ExistingCandidate> existing)
    {
        var results = new MatchResult?[incoming.Count];
        var used = new HashSet<int>();

        // 1. Même identifiant : doublon exact. Une opération qui passe d'« en cours » à débitée met son statut à jour.
        for (var i = 0; i < incoming.Count; i++)
        {
            var hit = existing.FirstOrDefault(e => e.ProviderId == incoming[i].Id && !used.Contains(e.Id));
            if (hit is null) continue;
            used.Add(hit.Id);
            results[i] = hit.IsPending && !incoming[i].Tx.Pending
                ? new MatchResult(MatchKind.Reconcile, hit.Id, false)
                : new MatchResult(MatchKind.Duplicate, hit.Id, false);
        }

        // 2. Même montant, date proche, libellé proche.
        for (var i = 0; i < incoming.Count; i++)
        {
            if (results[i] is not null) continue;
            var tx = incoming[i].Tx;
            var tokens = Tokens(string.Join(' ', tx.Counterparty, tx.Remittance));

            var scored = existing
                .Where(e => !used.Contains(e.Id) && e.Amount == tx.Amount)
                .Select(e => (E: e, Diff: Math.Abs(e.Date.DayNumber - tx.Date.DayNumber), Overlap: Overlaps(tokens, Tokens(e.Text))))
                .Where(x => x.E.IsManual
                    ? x.Diff <= WindowDays
                    : x.E.IsPending || x.E.Adoptable ? x.Diff <= WindowDays && x.Overlap
                    : x.Diff == 0 && x.Overlap)   // opération déjà débitée : même jour et même enseigne
                .OrderBy(x => x.Diff).ThenByDescending(x => x.Overlap).ThenBy(x => x.E.IsManual ? 1 : 0)
                .ToList();

            if (scored.Count == 0) { results[i] = new MatchResult(MatchKind.New, null, false); continue; }
            var best = scored[0];
            var ambiguous = scored.Count > 1 && scored[1].Diff == best.Diff && scored[1].Overlap == best.Overlap;
            used.Add(best.E.Id);
            var booked = !best.E.IsManual && !best.E.IsPending && !best.E.Adoptable;
            results[i] = new MatchResult(booked ? MatchKind.Duplicate : MatchKind.Reconcile, best.E.Id, ambiguous && !booked);
        }
        return results!;
    }

    /// <summary>Fusionne une opération entrante dans une saisie manuelle ou une opération en attente.</summary>
    public static void Merge(Transaction existing, ParsedTransaction row, string id)
    {
        existing.BookingDate = row.Date;
        existing.Status = row.Pending ? TransactionStatus.Pending : TransactionStatus.Booked;
        existing.ProviderTransactionId = id;
        existing.Counterparty ??= row.Counterparty;
        existing.RemittanceInfo ??= row.Remittance;
    }

    public static ExistingCandidate ToCandidate(Transaction t) => ToCandidate(t, false);

    /// <param name="forBankSync">Vrai pour la synchro bancaire : les opérations collées ou importées deviennent adoptables.</param>
    public static ExistingCandidate ToCandidate(Transaction t, bool forBankSync) => new(
        t.Id, t.BookingDate, t.Amount, t.IsManual, t.Status == TransactionStatus.Pending, t.ProviderTransactionId,
        string.Join(' ', t.Note, t.Counterparty, t.RemittanceInfo),
        forBankSync && !t.IsManual && t.Status != TransactionStatus.Pending && !Banking.BankFeed.IsFromSync(t.ProviderTransactionId));

    private static readonly HashSet<string> Noise = new() { "CB", "CARTE", "PAIEMENT", "PRLV", "VIR", "VIREMENT", "ACHAT", "LIGNE", "FACTURE", "SEPA", "PAYMENTS", "PAYMENT", "ACHAT" };

    private static HashSet<string> Tokens(string? s) =>
        TextNormalizer.Normalize(s).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetter).ToArray()))
            .Where(w => w.Length >= 3 && !Noise.Contains(w))
            .ToHashSet();

    /// <summary>Vrai si les deux textes partagent au moins un mot significatif (CB, VIR, etc. ignorés).</summary>
    public static bool TextOverlap(string? a, string? b) => Overlaps(Tokens(a), Tokens(b));

    private static bool Overlaps(HashSet<string> a, HashSet<string> b) =>
        a.Any(x => b.Any(y => x == y || (Math.Min(x.Length, y.Length) >= 4 && (x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))));
}
