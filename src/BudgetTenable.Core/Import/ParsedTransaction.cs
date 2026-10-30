using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BudgetTenable.Core.Rules;

namespace BudgetTenable.Core.Import;

public sealed record ParsedTransaction(
    DateOnly Date,
    DateOnly? ValueDate,
    decimal Amount,
    string? Counterparty,
    string? Remittance,
    /// <summary>Identifiant fourni par la banque (FITID OFX), s'il existe.</summary>
    string? ProviderId);

public sealed class ImportFormatException(string message) : Exception(message);

public static class Fingerprint
{
    /// <summary>
    /// Empreinte (date, montant, contrepartie, libellé). Le rang d'occurrence distingue deux opérations
    /// identiques le même jour (deux cafés au même prix).
    /// </summary>
    public static string Of(ParsedTransaction t, int occurrence)
    {
        var raw = string.Join('|',
            t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            t.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            TextNormalizer.Normalize(t.Counterparty),
            TextNormalizer.Normalize(t.Remittance),
            occurrence);
        return "fp:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..32].ToLowerInvariant();
    }

    /// <summary>Attribue à chaque ligne son identifiant stable : celui de la banque, sinon l'empreinte.</summary>
    public static IReadOnlyList<(ParsedTransaction Tx, string Id)> Assign(IEnumerable<ParsedTransaction> rows)
    {
        var seen = new Dictionary<string, int>();
        var result = new List<(ParsedTransaction, string)>();
        foreach (var t in rows)
        {
            if (!string.IsNullOrWhiteSpace(t.ProviderId)) { result.Add((t, t.ProviderId!)); continue; }
            var baseKey = Of(t, 0);
            var n = seen.GetValueOrDefault(baseKey);
            seen[baseKey] = n + 1;
            result.Add((t, n == 0 ? baseKey : Of(t, n)));
        }
        return result;
    }
}

public static class StatementDecoder
{
    /// <summary>UTF-8 (avec ou sans BOM) si valide, sinon Windows-1252/Latin-1 comme beaucoup d'exports bancaires.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }
}
