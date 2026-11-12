using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BudgetTenable.Core.Import;

namespace BudgetTenable.Core.Banking;

/// <summary>
/// Traduit les opérations renvoyées par l'agrégateur en opérations de l'appli et leur donne un identifiant stable.
/// </summary>
public static class BankFeed
{
    /// <summary>Identifiant bâti sur la référence de la banque (<c>entry_reference</c>).</summary>
    public const string ReferencePrefix = "eb:";
    /// <summary>Identifiant bâti sur une empreinte, quand la banque ne fournit pas de référence.</summary>
    public const string FingerprintPrefix = "ebf:";
    private const int MaxReferenceLength = 120;

    /// <summary>Vrai si l'opération a été créée (ou adoptée) par la synchro bancaire.</summary>
    public static bool IsFromSync(string? providerId) =>
        providerId is not null && (providerId.StartsWith(ReferencePrefix, StringComparison.Ordinal) || providerId.StartsWith(FingerprintPrefix, StringComparison.Ordinal));

    /// <summary>
    /// Opération de l'appli, ou null si elle ne compte pas : annulée, rejetée, programmée (pas encore passée),
    /// ou illisible (sans date ou sans montant). Dans le résultat, <c>ProviderId</c> porte la référence brute de la banque.
    /// </summary>
    public static ParsedTransaction? ToParsed(EbTransaction t)
    {
        var status = (t.Status ?? "").Trim().ToUpperInvariant();
        if (status is "CNCL" or "RJCT" or "SCHD") return null;

        if (!decimal.TryParse(t.TransactionAmount?.Amount, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)) return null;
        var booking = Date(t.BookingDate);
        var date = booking ?? Date(t.TransactionDate) ?? Date(t.ValueDate);
        if (date is null) return null;

        // Le sens vient de l'indicateur, jamais du signe : certaines banques envoient des montants déjà signés.
        var credit = string.Equals(t.CreditDebitIndicator?.Trim(), "CRDT", StringComparison.OrdinalIgnoreCase);
        amount = credit ? Math.Abs(amount) : -Math.Abs(amount);

        // « BOOK » = débitée. « PDNG » et « HOLD » = en attente. Statut inconnu : débitée seulement si la banque donne une date comptable.
        var pending = status is "PDNG" or "HOLD" || (status != "BOOK" && booking is null);

        var counterparty = Clean(credit ? t.Debtor?.Name : t.Creditor?.Name);
        var remittance = Clean(t.RemittanceInformation is null ? null : string.Join(' ', t.RemittanceInformation.Where(s => !string.IsNullOrWhiteSpace(s))));
        if (counterparty is null && remittance is null) remittance = Clean(t.BankTransactionCode?.Description);

        return new ParsedTransaction(date.Value, Date(t.ValueDate), amount, counterparty, remittance, Clean(t.EntryReference), Pending: pending);
    }

    /// <summary>
    /// Attribue à chaque opération son identifiant stable.
    /// Avec référence bancaire : <c>eb:référence</c>. Deux lignes identiques de même référence sont la même opération
    /// renvoyée deux fois (chevauchement de pages) : une seule est gardée. Deux lignes différentes de même référence
    /// (défaut connu de certaines banques) sont gardées toutes les deux, la seconde avec un suffixe.
    /// Sans référence : empreinte (date, montant, contrepartie, libellé) avec un rang pour deux opérations identiques.
    /// </summary>
    public static IReadOnlyList<(ParsedTransaction Tx, string Id)> Assign(IEnumerable<EbTransaction> transactions)
    {
        var rows = transactions.Select(ToParsed).Where(t => t is not null).Select(t => t!).ToList();
        var result = new List<(ParsedTransaction, string)>(rows.Count);

        foreach (var group in rows.Where(r => r.ProviderId is not null).GroupBy(r => r.ProviderId!, StringComparer.Ordinal))
        {
            var id = ReferencePrefix + (group.Key.Length <= MaxReferenceLength ? group.Key : "#" + Hash(group.Key));
            var distinct = group.GroupBy(r => Fingerprint.Of(r, 0), StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            for (var i = 0; i < distinct.Count; i++)
                result.Add((distinct[i].First(), i == 0 ? id : id + "~" + distinct[i].Key[3..11]));
        }

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => r.ProviderId is null))
        {
            var key = Fingerprint.Of(row, 0);
            var n = seen.GetValueOrDefault(key);
            seen[key] = n + 1;
            result.Add((row, FingerprintPrefix + Fingerprint.Of(row, n)[3..]));
        }
        return result;
    }

    /// <summary>IBAN réduit à son pays et ses quatre derniers caractères : « FR•• •••• 1234 ».</summary>
    public static string? MaskIban(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var s = new string(iban.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length < 8 ? "••••" : $"{s[..2]}•• •••• {s[^4..]}";
    }

    private static DateOnly? Date(string? s) =>
        s is { Length: >= 10 } && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..32].ToLowerInvariant();
}
