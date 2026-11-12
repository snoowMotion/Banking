using BudgetTenable.Core.Budget;

namespace BudgetTenable.Tests;

public class PeriodTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    // Valeurs de l'oracle (fonction pkey du prototype, début de période le 28).
    [Theory]
    [InlineData("2026-10-27", "2026-09")]
    [InlineData("2026-10-28", "2026-10")]
    [InlineData("2026-11-27", "2026-10")]
    [InlineData("2026-12-31", "2026-12")]
    [InlineData("2027-01-01", "2026-12")]
    [InlineData("2027-01-27", "2026-12")]
    [InlineData("2027-01-28", "2027-01")]
    public void A_date_belongs_to_the_period_that_contains_it(string date, string expected) =>
        Assert.Equal(expected, Periods.KeyFor(D(date), 28).ToString());

    // Périodes à cheval sur deux mois, février, année bissextile (2028), passage à l'heure d'été (29 mars 2026)
    // et à l'heure d'hiver (25 octobre 2026) : les longueurs sont celles du prototype.
    [Theory]
    [InlineData("2026-02", 28)]
    [InlineData("2026-03", 31)]   // contient le passage à l'heure d'été
    [InlineData("2026-10", 31)]   // 28 oct. → 27 nov.
    [InlineData("2027-02", 28)]
    [InlineData("2028-02", 29)]   // année bissextile
    [InlineData("2028-03", 31)]
    public void Period_length(string key, int expected) =>
        Assert.Equal(expected, Periods.Length(PeriodKey.Parse(key), 28));

    [Fact]
    public void Dst_changes_do_not_shift_period_boundaries()
    {
        // 28 mars → 27 avril 2026 contient le passage à l'heure d'été (29 mars).
        var k = PeriodKey.Parse("2026-03");
        Assert.Equal(D("2026-03-28"), Periods.Start(k, 28));
        Assert.Equal(D("2026-04-27"), Periods.LastDay(k, 28));
        Assert.Equal(D("2026-04-28"), Periods.EndExclusive(k, 28));
        // 28 oct. 2025 → 27 nov. 2025 vient juste après le retour à l'heure d'hiver.
        Assert.Equal(31, Periods.Length(PeriodKey.Parse("2025-10"), 28));
        // Tout jour de l'année tombe dans exactement une période.
        for (var d = D("2026-01-01"); d < D("2027-01-01"); d = d.AddDays(1))
        {
            var key = Periods.KeyFor(d, 28);
            Assert.True(d >= Periods.Start(key, 28) && d < Periods.EndExclusive(key, 28), d.ToString());
        }
    }

    [Theory]
    [InlineData("2026-10", "28 oct. → 27 nov. 2026")]
    [InlineData("2026-12", "28 déc. → 27 janv. 2027")]
    [InlineData("2028-02", "28 févr. → 27 mars 2028")]
    public void Label_matches_the_prototype(string key, string expected) =>
        Assert.Equal(expected, Periods.Label(PeriodKey.Parse(key), 28));

    [Fact]
    public void Period_starting_on_the_first_is_a_calendar_month()
    {
        var k = Periods.KeyFor(D("2026-11-01"), 1);
        Assert.Equal("2026-11", k.ToString());
        Assert.Equal(30, Periods.Length(k, 1));
        Assert.Equal("1 nov. → 30 nov. 2026", Periods.Label(k, 1));
    }

    [Fact]
    public void Elapsed_counts_today()
    {
        var k = PeriodKey.Parse("2026-10");
        Assert.Equal(1, Periods.Elapsed(k, 28, D("2026-10-28")));
        Assert.Equal(3, Periods.Elapsed(k, 28, D("2026-10-30")));
        Assert.Equal(31, Periods.Elapsed(k, 28, D("2026-11-27")));
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("abcd-ef")]
    [InlineData("")]
    public void Invalid_keys_are_rejected(string s) => Assert.False(PeriodKey.TryParse(s, out _));

    [Fact]
    public void AddMonths_crosses_years() =>
        Assert.Equal("2025-12", PeriodKey.Parse("2026-02").AddMonths(-2).ToString());
}
