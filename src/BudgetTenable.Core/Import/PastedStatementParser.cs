using System.Text.RegularExpressions;
using BudgetTenable.Core.Rules;

namespace BudgetTenable.Core.Import;

public sealed record PasteParseResult(IReadOnlyList<ParsedTransaction> Items, int Ignored);

/// <summary>
/// Lit des opérations copiées depuis le site d'une banque, un bloc par opération :
/// <code>
/// 1. ANTHROPIC CLAUDE SUB
/// lun. 2 nov. à 10h33 - Achat en ligne - États-Unis d'Amérique
/// High-Tech/Electroménager
/// Carte bancaire
/// En cours
/// -21,60 €
/// </code>
/// Le bloc est repéré par sa ligne de date (le libellé est la ligne qui la précède, la numérotation est facultative)
/// et se termine à la ligne de montant. L'année, absente, se déduit du jour de la semaine.
/// </summary>
public static partial class PastedStatementParser
{
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    [GeneratedRegex(@"^(?:(?<wd>lun|mar|mer|jeu|ven|sam|dim)[a-zéû]*\.?\s+)?(?<d>\d{1,2})(?:er)?\s+(?<m>[a-zéûèôî]+)\.?(?:\s+(?<y>\d{4}))?(?:\s*(?:à|a|,)?\s*(?<h>\d{1,2})\s*[h:]\s*(?<min>\d{2}))?(?:\s*[-–]\s*(?<detail>.+))?$", Opt)]
    private static partial Regex DateLine();

    [GeneratedRegex(@"^(?<rel>aujourd['’]hui|hier)(?:\s*(?:à|a|,)?\s*(?<h>\d{1,2})\s*[h:]\s*(?<min>\d{2}))?(?:\s*[-–]\s*(?<detail>.+))?$", Opt)]
    private static partial Regex RelativeDateLine();

    [GeneratedRegex(@"^[+\-]?\s*\d[\d\s.,]*\s*(?:€|eur(?:os?)?)$", Opt)]
    private static partial Regex AmountLine();

    [GeneratedRegex(@"^(en cours|comptabilis|d[ée]bit|trait[ée]|effectu|annul|refus|rejet|en attente)", Opt)]
    private static partial Regex StatusLine();

    [GeneratedRegex(@"^(carte|pr[ée]l[èe]vement|virement|ch[èe]que|esp[èe]ces|retrait|paiement|paypal|apple pay|google pay|mandat|remise|d[ée]p[ôo]t|tip|sepa)", Opt)]
    private static partial Regex MethodLine();

    [GeneratedRegex(@"^\d{1,3}\s*[.)]\s*")]
    private static partial Regex Numbering();


    public static PasteParseResult Parse(string text, DateOnly today)
    {
        var lines = text.Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace('\u2212', '-').Replace('\u2013', '-')
            .Split('\n')
            .Select(l => l.Trim().TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        var items = new List<ParsedTransaction>();
        var ignored = 0;
        var occurrences = new Dictionary<string, int>();

        for (var i = 0; i < lines.Count; i++)
        {
            if (!TryDateLine(lines[i], today, out var date, out var time, out var detail, out var dateOk)) continue;
            if (!dateOk || i == 0 || IsAmount(lines[i - 1]) || IsDate(lines[i - 1], today)) { ignored++; continue; }

            var label = Numbering().Replace(lines[i - 1], "").Trim();
            var mid = new List<string>();
            var j = i + 1;
            while (j < lines.Count && j <= i + 6 && !IsAmount(lines[j]) && !IsDate(lines[j], today)) mid.Add(lines[j++]);
            if (label.Length == 0 || j >= lines.Count || !IsAmount(lines[j])) { ignored++; continue; }
            if (!AmountDate.TryParseAmount(lines[j], out var amount) || amount == 0) { ignored++; i = j; continue; }

            var status = mid.LastOrDefault(l => StatusLine().IsMatch(l));
            if (status is not null) mid.Remove(status);
            var method = mid.FirstOrDefault(l => MethodLine().IsMatch(l));
            if (method is not null) mid.Remove(method);
            var bankCategory = mid.FirstOrDefault();
            i = j;

            if (status is not null && Regex.IsMatch(status, "^(annul|refus|rejet)", Opt)) { ignored++; continue; }

            var item = new ParsedTransaction(date, null, amount, AmountDate.Clean(label), AmountDate.Clean(detail), null,
                AmountDate.Clean(bankCategory), status is not null && Regex.IsMatch(status, "^(en cours|en attente)", Opt), time, AmountDate.Clean(method));
            items.Add(item with { ProviderId = IdOf(item, occurrences) });
        }
        return new PasteParseResult(items, ignored);
    }

    /// <summary>Identifiant stable d'une opération collée (date, heure, montant, libellé), avec rang pour deux opérations identiques.</summary>
    private static string IdOf(ParsedTransaction t, Dictionary<string, int> seen)
    {
        var key = string.Join('|', t.Date.ToString("yyyy-MM-dd"), t.Time?.ToString("HH:mm") ?? "", t.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), TextNormalizer.Normalize(t.Counterparty));
        var n = seen.GetValueOrDefault(key);
        seen[key] = n + 1;
        var raw = n == 0 ? key : key + "|" + n;
        return "paste:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..24].ToLowerInvariant();
    }

    private static bool IsAmount(string line) => AmountLine().IsMatch(line);
    private static bool IsDate(string line, DateOnly today) => TryDateLine(line, today, out _, out _, out _, out _);

    /// <returns>Vrai si la ligne a la forme d'une date ; <paramref name="ok"/> dit si elle est exploitable.</returns>
    private static bool TryDateLine(string line, DateOnly today, out DateOnly date, out TimeOnly? time, out string? detail, out bool ok)
    {
        date = default; time = null; detail = null; ok = false;

        var rel = RelativeDateLine().Match(line);
        if (rel.Success)
        {
            date = rel.Groups["rel"].Value.StartsWith("hier", StringComparison.OrdinalIgnoreCase) ? today.AddDays(-1) : today;
            time = ReadTime(rel);
            detail = rel.Groups["detail"].Success ? rel.Groups["detail"].Value : null;
            ok = true;
            return true;
        }

        var m = DateLine().Match(line);
        if (!m.Success) return false;
        var month = MonthOf(m.Groups["m"].Value);
        if (month == 0) return false;
        var day = int.Parse(m.Groups["d"].Value);
        DayOfWeek? weekday = m.Groups["wd"].Success ? WeekdayOf(m.Groups["wd"].Value) : null;
        int? year = m.Groups["y"].Success ? int.Parse(m.Groups["y"].Value) : null;

        time = ReadTime(m);
        detail = m.Groups["detail"].Success ? m.Groups["detail"].Value : null;
        var inferred = InferDate(day, month, weekday, year, today);
        if (inferred is { } d) { date = d; ok = true; }
        return true;
    }

    private static TimeOnly? ReadTime(Match m)
    {
        if (!m.Groups["h"].Success) return null;
        int h = int.Parse(m.Groups["h"].Value), min = int.Parse(m.Groups["min"].Value);
        return h < 24 && min < 60 ? new TimeOnly(h, min) : null;
    }

    /// <summary>
    /// Année absente : on retient la date la plus récente (au plus 2 jours dans le futur) qui tombe le bon jour de la semaine.
    /// </summary>
    public static DateOnly? InferDate(int day, int month, DayOfWeek? weekday, int? year, DateOnly today)
    {
        if (year is { } y) return Valid(y, month, day);

        var candidates = new[] { today.Year, today.Year - 1, today.Year + 1 }
            .Select(yy => Valid(yy, month, day))
            .Where(d => d is not null && d.Value <= today.AddDays(2))
            .Select(d => d!.Value)
            .OrderByDescending(d => d)
            .ToList();
        return candidates.FirstOrDefault(d => weekday is null || d.DayOfWeek == weekday) is { } hit && hit != default
            ? hit
            : candidates.Count > 0 ? candidates[0] : null;   // jour de la semaine incohérent : on garde la date la plus récente
    }

    private static DateOnly? Valid(int year, int month, int day) =>
        year is >= 1900 and <= 2999 && day >= 1 && day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;

    private static int MonthOf(string token)
    {
        var t = TextNormalizer.Normalize(token);
        return t switch
        {
            _ when t.StartsWith("JANV") => 1,
            _ when t.StartsWith("FEVR") => 2,
            _ when t.StartsWith("MARS") => 3,
            _ when t.StartsWith("AVR") => 4,
            _ when t.StartsWith("MAI") => 5,
            _ when t.StartsWith("JUIN") => 6,
            _ when t.StartsWith("JUIL") => 7,
            _ when t.StartsWith("AOUT") => 8,
            _ when t.StartsWith("SEPT") => 9,
            _ when t.StartsWith("OCT") => 10,
            _ when t.StartsWith("NOV") => 11,
            _ when t.StartsWith("DEC") => 12,
            _ => 0,
        };
    }

    private static DayOfWeek WeekdayOf(string token) => TextNormalizer.Normalize(token)[..3] switch
    {
        "LUN" => DayOfWeek.Monday, "MAR" => DayOfWeek.Tuesday, "MER" => DayOfWeek.Wednesday, "JEU" => DayOfWeek.Thursday,
        "VEN" => DayOfWeek.Friday, "SAM" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday,
    };
}
