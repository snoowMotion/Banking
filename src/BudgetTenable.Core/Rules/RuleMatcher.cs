using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Core.Rules;

public static class TextNormalizer
{
    /// <summary>Majuscules, sans accents, espaces compactés : « Prélèvement  Free » devient « PRELEVEMENT FREE ».</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        var lastSpace = false;
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch))
            {
                if (!lastSpace && sb.Length > 0) sb.Append(' ');
                lastSpace = true;
            }
            else
            {
                sb.Append(char.ToUpperInvariant(ch));
                lastSpace = false;
            }
        }
        return sb.ToString().TrimEnd();
    }
}

public static class RuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    /// <summary>Valide un motif ; renvoie un message d'erreur ou null.</summary>
    public static string? ValidatePattern(string pattern, bool isRegex)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "Le motif est vide.";
        if (!isRegex) return null;
        try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); return null; }
        catch (ArgumentException e) { return "Expression régulière invalide : " + e.Message; }
    }

    public static bool Matches(Rule rule, Transaction tx)
    {
        if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Pattern)) return false;

        switch (rule.Direction)
        {
            case RuleDirection.Debit when tx.Amount >= 0:
            case RuleDirection.Credit when tx.Amount <= 0:
                return false;
        }
        var abs = Math.Abs(tx.Amount);
        if (rule.MinAmount is { } min && abs < min) return false;
        if (rule.MaxAmount is { } max && abs > max) return false;

        var text = rule.Field switch
        {
            RuleField.Counterparty => tx.Counterparty,
            RuleField.Remittance => tx.RemittanceInfo,
            _ => string.Join(' ', new[] { tx.Counterparty, tx.RemittanceInfo }.Where(x => !string.IsNullOrWhiteSpace(x))),
        };
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (rule.IsRegex)
        {
            try { return Regex.IsMatch(text, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout); }
            catch (RegexMatchTimeoutException) { return false; }
            catch (ArgumentException) { return false; }
        }
        return TextNormalizer.Normalize(text).Contains(TextNormalizer.Normalize(rule.Pattern), StringComparison.Ordinal);
    }

    /// <summary>Règles par priorité décroissante, puis par ancienneté.</summary>
    public static List<Rule> Ordered(IEnumerable<Rule> rules) =>
        rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority).ThenBy(r => r.Id).ToList();

    /// <summary>
    /// Applique la première règle qui correspond. Ne touche jamais à un classement manuel.
    /// Renvoie vrai si la transaction a été classée.
    /// </summary>
    public static bool Apply(Transaction tx, IReadOnlyList<Rule> orderedRules)
    {
        if (tx.CategorySource == CategorySource.Manual) return false;
        foreach (var rule in orderedRules)
        {
            if (!Matches(rule, tx)) continue;
            tx.CategoryId = rule.CategoryId;
            tx.IsInternalTransfer = rule.MarksInternalTransfer;
            tx.CategorySource = CategorySource.Rule;
            return true;
        }
        return false;
    }

    private static readonly HashSet<string> NoiseWords = new()
    {
        "CB", "CARTE", "PAIEMENT", "PAIEMT", "PRLV", "PRELEVEMENT", "VIR", "VIREMENT", "FACTURE", "ACHAT", "RETRAIT", "SEPA", "DE", "DU", "LE", "LA",
    };

    /// <summary>
    /// Motif proposé après un tri manuel : les deux premiers mots significatifs de la contrepartie ou du libellé,
    /// pris consécutivement pour que le motif « contient » retrouve bien le libellé d'origine.
    /// </summary>
    public static string SuggestPattern(Transaction tx)
    {
        var source = !string.IsNullOrWhiteSpace(tx.Counterparty) ? tx.Counterparty : tx.RemittanceInfo;
        var tokens = TextNormalizer.Normalize(source).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var picked = tokens
            .SkipWhile(t => NoiseWords.Contains(t))
            .TakeWhile(t => t.Count(char.IsLetter) >= 3)
            .Take(2);
        return string.Join(' ', picked);
    }
}
