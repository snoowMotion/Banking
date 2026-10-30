using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Rules;

namespace BudgetTenable.Tests;

public class RuleTests
{
    private static Transaction Tx(string counterparty, decimal amount = -10m, string? remittance = null) =>
        new() { Counterparty = counterparty, RemittanceInfo = remittance, Amount = amount };

    private static Rule R(string pattern, string? cat = "courses", int priority = 0, Action<Rule>? tweak = null)
    {
        var r = new Rule { Id = 1, Pattern = pattern, CategoryId = cat, Priority = priority };
        tweak?.Invoke(r);
        return r;
    }

    [Theory]
    [InlineData("leclerc", "CB E.LECLERC LILLE", true)]
    [InlineData("prélèvement", "PRELEVEMENT FREE", true)]
    [InlineData("LECLERC", "carrefour", false)]
    public void Contains_ignores_case_and_accents(string pattern, string text, bool expected) =>
        Assert.Equal(expected, RuleMatcher.Matches(R(pattern), Tx(text)));

    [Fact]
    public void Regex_rules_match_and_invalid_regex_never_throws()
    {
        Assert.True(RuleMatcher.Matches(R(@"^CB\s+AMAZON", tweak: r => r.IsRegex = true), Tx("CB AMAZON PAYMENTS")));
        Assert.False(RuleMatcher.Matches(R("([", tweak: r => r.IsRegex = true), Tx("CB AMAZON")));
        Assert.NotNull(RuleMatcher.ValidatePattern("([", true));
        Assert.Null(RuleMatcher.ValidatePattern("amazon", false));
    }

    [Fact]
    public void Direction_and_amount_range_restrict_a_rule()
    {
        var debitOnly = R("caf", tweak: r => r.Direction = RuleDirection.Debit);
        Assert.False(RuleMatcher.Matches(debitOnly, Tx("CAF", 198m)));
        Assert.True(RuleMatcher.Matches(debitOnly, Tx("CAF", -198m)));

        var range = R("paypal", tweak: r => { r.MinAmount = 50; r.MaxAmount = 70; });
        Assert.True(RuleMatcher.Matches(range, Tx("PAYPAL", -60m)));
        Assert.False(RuleMatcher.Matches(range, Tx("PAYPAL", -9.99m)));
        Assert.False(RuleMatcher.Matches(range, Tx("PAYPAL", -120m)));
    }

    [Fact]
    public void Field_selects_where_the_pattern_is_searched()
    {
        var tx = Tx("DUPONT", -5m, "LOYER OCTOBRE");
        Assert.True(RuleMatcher.Matches(R("loyer", tweak: r => r.Field = RuleField.Remittance), tx));
        Assert.False(RuleMatcher.Matches(R("loyer", tweak: r => r.Field = RuleField.Counterparty), tx));
        Assert.True(RuleMatcher.Matches(R("loyer"), tx));
    }

    [Fact]
    public void Highest_priority_wins_and_manual_choices_are_never_overwritten()
    {
        var rules = RuleMatcher.Ordered(new[]
        {
            new Rule { Id = 1, Pattern = "amazon", CategoryId = "shop", Priority = 1 },
            new Rule { Id = 2, Pattern = "amazon prime", CategoryId = "abo", Priority = 5 },
        });
        var tx = Tx("AMAZON PRIME VIDEO");
        Assert.True(RuleMatcher.Apply(tx, rules));
        Assert.Equal("abo", tx.CategoryId);
        Assert.Equal(CategorySource.Rule, tx.CategorySource);

        var manual = Tx("AMAZON PRIME VIDEO");
        manual.CategoryId = "jeux";
        manual.CategorySource = CategorySource.Manual;
        Assert.False(RuleMatcher.Apply(manual, rules));
        Assert.Equal("jeux", manual.CategoryId);
    }

    [Fact]
    public void Internal_transfer_rules_flag_the_transaction()
    {
        var rules = RuleMatcher.Ordered(new[] { new Rule { Id = 1, Pattern = "virement ldds", CategoryId = null, MarksInternalTransfer = true } });
        var tx = Tx("VIREMENT LDDS");
        Assert.True(RuleMatcher.Apply(tx, rules));
        Assert.True(tx.IsInternalTransfer);
        Assert.Null(tx.CategoryId);
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var rules = RuleMatcher.Ordered(new[] { new Rule { Id = 1, Pattern = "x", CategoryId = "shop", Enabled = false } });
        Assert.Empty(rules);
    }

    [Fact]
    public void Suggested_pattern_keeps_the_first_meaningful_words_and_matches_the_original()
    {
        var a = Tx("CB E.Leclerc Lille 29/10");
        Assert.Equal("E.LECLERC LILLE", RuleMatcher.SuggestPattern(a));
        Assert.True(RuleMatcher.Matches(R(RuleMatcher.SuggestPattern(a)), a));

        var b = new Transaction { RemittanceInfo = "PAIEMENT CB AMAZON PAYMENTS 1234", Amount = -20m };
        Assert.Equal("AMAZON PAYMENTS", RuleMatcher.SuggestPattern(b));
        Assert.True(RuleMatcher.Matches(R(RuleMatcher.SuggestPattern(b)), b));

        Assert.Equal("", RuleMatcher.SuggestPattern(new Transaction()));
    }
}
