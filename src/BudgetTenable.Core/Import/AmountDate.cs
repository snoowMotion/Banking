using System.Globalization;
using System.Text.RegularExpressions;

namespace BudgetTenable.Core.Import;

public static partial class AmountDate
{
    private static readonly string[] DateFormats =
        { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yy", "d/M/yy", "yyyyMMdd" };

    public static bool TryParseDate(string? s, out DateOnly date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        // Horodatage ISO : on ne garde que la date locale telle qu'écrite.
        if (s.Length > 10 && (s[10] == 'T' || s[10] == ' ')) s = s[..10];
        return DateOnly.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Accepte « 1 234,56 », « 1.234,56 », « -12.50 », « (12,50) », « 12,50 € ».</summary>
    public static bool TryParseAmount(string? s, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim().Replace(" ", "").Replace(" ", "").Replace(" ", "").Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase);
        var negative = false;
        if (t.StartsWith('(') && t.EndsWith(')')) { negative = true; t = t[1..^1]; }
        if (t.StartsWith('+')) t = t[1..];
        if (t.Length == 0) return false;

        var lastComma = t.LastIndexOf(',');
        var lastDot = t.LastIndexOf('.');
        if (lastComma >= 0 && lastDot >= 0)
        {
            // Le dernier séparateur est la virgule décimale, l'autre sépare les milliers.
            t = lastComma > lastDot ? t.Replace(".", "").Replace(',', '.') : t.Replace(",", "");
        }
        else if (lastComma >= 0) t = t.Replace(',', '.');

        if (!decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value))
            return false;
        if (negative) value = -value;
        return true;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = Spaces().Replace(s.Trim(), " ");
        return t.Length == 0 ? null : t;
    }
}
