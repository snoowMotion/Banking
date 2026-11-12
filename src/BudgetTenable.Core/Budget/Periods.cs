using System.Globalization;

namespace BudgetTenable.Core.Budget;

/// <summary>Période de budget, nommée d'après son mois de début.</summary>
public readonly record struct PeriodKey(int Year, int Month) : IComparable<PeriodKey>
{
    public PeriodKey AddMonths(int n)
    {
        var total = Year * 12 + (Month - 1) + n;
        return new PeriodKey(total / 12, total % 12 + 1);
    }

    /// <summary>Nombre de mois de <paramref name="from"/> à <paramref name="to"/>.</summary>
    public static int MonthsBetween(PeriodKey from, PeriodKey to) => (to.Year - from.Year) * 12 + (to.Month - from.Month);

    public int CompareTo(PeriodKey other) => (Year * 12 + Month).CompareTo(other.Year * 12 + other.Month);
    public static bool operator <(PeriodKey a, PeriodKey b) => a.CompareTo(b) < 0;
    public static bool operator >(PeriodKey a, PeriodKey b) => a.CompareTo(b) > 0;
    public static bool operator <=(PeriodKey a, PeriodKey b) => a.CompareTo(b) <= 0;
    public static bool operator >=(PeriodKey a, PeriodKey b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Year:0000}-{Month:00}";

    public static bool TryParse(string? s, out PeriodKey key)
    {
        key = default;
        if (s is null || s.Length != 7 || s[4] != '-') return false;
        if (!int.TryParse(s.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y)) return false;
        if (!int.TryParse(s.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var m)) return false;
        if (y < 1900 || y > 2999 || m < 1 || m > 12) return false;
        key = new PeriodKey(y, m);
        return true;
    }

    public static PeriodKey Parse(string s) => TryParse(s, out var k) ? k : throw new FormatException($"Période invalide : {s}");
}

/// <summary>
/// Calculs de période en dates locales (<see cref="DateOnly"/>), jamais en UTC : le changement d'heure ne les touche pas.
/// Une période commence le jour <c>startDay</c> et finit la veille du même jour le mois suivant.
/// </summary>
public static class Periods
{
    private static readonly string[] MonthShort =
        { "janv.", "févr.", "mars", "avr.", "mai", "juin", "juil.", "août", "sept.", "oct.", "nov.", "déc." };

    public static bool IsValidStartDay(int day) => day is >= 1 and <= 28;

    public static PeriodKey KeyFor(DateOnly date, int startDay)
    {
        var k = new PeriodKey(date.Year, date.Month);
        return date.Day >= startDay ? k : k.AddMonths(-1);
    }

    public static DateOnly Start(PeriodKey key, int startDay) => new(key.Year, key.Month, startDay);
    public static DateOnly EndExclusive(PeriodKey key, int startDay) => Start(key.AddMonths(1), startDay);
    public static DateOnly LastDay(PeriodKey key, int startDay) => EndExclusive(key, startDay).AddDays(-1);
    public static int Length(PeriodKey key, int startDay) => EndExclusive(key, startDay).DayNumber - Start(key, startDay).DayNumber;

    /// <summary>Jours écoulés depuis le début, aujourd'hui compris. Zéro ou négatif si la période n'a pas commencé.</summary>
    public static int Elapsed(PeriodKey key, int startDay, DateOnly today) => today.DayNumber - Start(key, startDay).DayNumber + 1;

    public static string Label(PeriodKey key, int startDay)
    {
        var a = Start(key, startDay);
        var e = LastDay(key, startDay);
        return $"{a.Day} {MonthShort[a.Month - 1]} → {e.Day} {MonthShort[e.Month - 1]} {e.Year}";
    }
}
