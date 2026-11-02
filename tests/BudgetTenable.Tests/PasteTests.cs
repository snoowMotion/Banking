using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Import;

namespace BudgetTenable.Tests;

public class PastedStatementParserTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static readonly DateOnly Today = D("2026-11-02");

    // Texte tel que collé depuis le site de la banque.
    public const string Sample = """
        1. ANTHROPIC CLAUDE SUB
        lun. 2 nov. à 10h33 - Achat en ligne - États-Unis d'Amérique
        High-Tech/Electroménager
        Carte bancaire
        En cours
        -21,60 €
        2. SEPHORA NOYELLES GODAULT
        dim. 1 nov. à 17h44
        Coiffeurs et soins du corps
        Carte bancaire
        En cours
        -86,25 €
        3. AMAZON
        dim. 1 nov. à 15h20 - Achat en ligne
        Shopping et services - autre
        Carte bancaire
        En cours
        -15,99 €
        4. AMAZON
        dim. 1 nov. à 14h05 - Achat en ligne
        Shopping et services - autre
        Carte bancaire
        En cours
        -4,99 €
        5. NOZ
        sam. 31 oct. à 16h31 - COURRIERES
        Loyers et traites
        Carte bancaire
        En cours
        -28,99 €
        """;

    [Fact]
    public void Parses_the_five_operations_of_the_example()
    {
        var r = PastedStatementParser.Parse(Sample, Today);
        Assert.Equal(0, r.Ignored);
        Assert.Equal(5, r.Items.Count);

        var a = r.Items[0];
        Assert.Equal("ANTHROPIC CLAUDE SUB", a.Counterparty);
        Assert.Equal(D("2026-11-02"), a.Date);
        Assert.Equal(new TimeOnly(10, 33), a.Time);
        Assert.Equal("Achat en ligne - États-Unis d'Amérique", a.Remittance);
        Assert.Equal("High-Tech/Electroménager", a.BankCategory);
        Assert.Equal("Carte bancaire", a.PaymentMethod);
        Assert.True(a.Pending);
        Assert.Equal(-21.60m, a.Amount);

        Assert.Equal("SEPHORA NOYELLES GODAULT", r.Items[1].Counterparty);
        Assert.Null(r.Items[1].Remittance);                       // pas de détail sur cette ligne
        Assert.Equal(D("2026-11-01"), r.Items[1].Date);
        Assert.Equal("Coiffeurs et soins du corps", r.Items[1].BankCategory);
        Assert.Equal(-86.25m, r.Items[1].Amount);

        Assert.Equal(new[] { -21.60m, -86.25m, -15.99m, -4.99m, -28.99m }, r.Items.Select(i => i.Amount));
        Assert.Equal("COURRIERES", r.Items[4].Remittance);
        Assert.Equal(D("2026-10-31"), r.Items[4].Date);
        Assert.Equal("Loyers et traites", r.Items[4].BankCategory);
    }

    [Fact]
    public void Each_operation_gets_a_stable_distinct_id_and_the_same_text_gives_the_same_ids()
    {
        var a = PastedStatementParser.Parse(Sample, Today).Items.Select(i => i.ProviderId).ToList();
        var b = PastedStatementParser.Parse(Sample, Today).Items.Select(i => i.ProviderId).ToList();
        Assert.Equal(a, b);
        Assert.Equal(5, a.Distinct().Count());
        Assert.All(a, id => Assert.StartsWith("paste:", id));
    }

    [Fact]
    public void Two_identical_operations_get_distinct_ids()
    {
        const string twice = "CAFE\nlun. 2 nov. à 09h00\nRestaurants\nCarte bancaire\nEn cours\n-2,50 €\nCAFE\nlun. 2 nov. à 09h00\nRestaurants\nCarte bancaire\nEn cours\n-2,50 €";
        var items = PastedStatementParser.Parse(twice, Today).Items;
        Assert.Equal(2, items.Count);
        Assert.NotEqual(items[0].ProviderId, items[1].ProviderId);
    }

    [Fact]
    public void The_weekday_finds_the_year_across_new_year()
    {
        // Le 3 janvier 2027 est un dimanche ; mer. 30 déc. est donc en 2026.
        var today = D("2027-01-03");
        Assert.Equal(D("2026-12-30"), PastedStatementParser.InferDate(30, 12, DayOfWeek.Wednesday, null, today));
        Assert.Equal(D("2027-01-01"), PastedStatementParser.InferDate(1, 1, DayOfWeek.Friday, null, today));
        // Sans jour de la semaine : la date la plus récente qui n'est pas dans le futur.
        Assert.Equal(D("2026-12-30"), PastedStatementParser.InferDate(30, 12, null, null, today));
        // 29 février : seule 2028 est bissextile.
        Assert.Equal(D("2028-02-29"), PastedStatementParser.InferDate(29, 2, DayOfWeek.Tuesday, null, D("2028-03-05")));
        Assert.Equal(D("2026-05-04"), PastedStatementParser.InferDate(4, 5, null, 2026, today));
    }

    [Fact]
    public void Works_without_numbering_without_status_and_with_credits_and_odd_spacing()
    {
        const string text = "\n  VIR CAF  \nmer. 28 oct.\nAllocations\nVirement\n+198,00 €\n\nLECLERC\nmar. 27 oct. à 18h02 - LILLE\nSupermarchés\nCarte bancaire\n-1 234,56 €\n";
        var r = PastedStatementParser.Parse(text, Today);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(198m, r.Items[0].Amount);
        Assert.False(r.Items[0].Pending);
        Assert.Equal("Virement", r.Items[0].PaymentMethod);
        Assert.Equal("Allocations", r.Items[0].BankCategory);
        Assert.Equal(-1234.56m, r.Items[1].Amount);
        Assert.Equal(D("2026-10-27"), r.Items[1].Date);
    }

    [Fact]
    public void Handles_today_and_yesterday_and_the_typographic_minus()
    {
        const string text = "BOULANGERIE\nAujourd'hui à 08h15\nAlimentation\nCarte bancaire\nEn cours\n−3,20 €\nPHARMACIE\nHier\nSanté\nCarte bancaire\n-12,00 €";
        var r = PastedStatementParser.Parse(text, Today);
        Assert.Equal(D("2026-11-02"), r.Items[0].Date);
        Assert.Equal(-3.20m, r.Items[0].Amount);
        Assert.Equal(D("2026-11-01"), r.Items[1].Date);
    }

    [Fact]
    public void Garbage_and_incomplete_blocks_are_counted_not_fatal()
    {
        const string text = "Mes opérations\nFiltrer\nORPHELIN\nlun. 2 nov. à 10h00\nCarte bancaire\nEn cours\nOK\nlun. 2 nov. à 11h00\nCarte bancaire\n-5,00 €\nTotal : 3 opérations";
        var r = PastedStatementParser.Parse(text, Today);
        Assert.Single(r.Items);
        Assert.Equal("OK", r.Items[0].Counterparty);
        Assert.Equal(1, r.Ignored);
        Assert.Empty(PastedStatementParser.Parse("rien d'utile ici", Today).Items);
    }

    [Fact]
    public void Cancelled_operations_are_ignored()
    {
        var r = PastedStatementParser.Parse("X\nlun. 2 nov.\nCarte bancaire\nAnnulée\n-5,00 €", Today);
        Assert.Empty(r.Items);
        Assert.Equal(1, r.Ignored);
    }
}

public class ReconcilerTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);
    private static (ParsedTransaction, string) In(string date, decimal amount, string label, bool pending = false, string id = "paste:new") =>
        (new ParsedTransaction(D(date), null, amount, label, null, id, null, pending), id);
    private static ExistingCandidate Ex(int id, string date, decimal amount, string text, bool manual = false, bool pending = false, string? providerId = null) =>
        new(id, D(date), amount, manual, pending, providerId, text);

    [Fact]
    public void Same_id_is_a_duplicate_unless_a_pending_operation_is_now_booked()
    {
        var same = Reconciler.Match(new[] { In("2026-11-02", -5m, "X", id: "a") }, new[] { Ex(1, "2026-11-02", -5m, "X", providerId: "a") });
        Assert.Equal(MatchKind.Duplicate, same[0].Kind);

        var nowBooked = Reconciler.Match(new[] { In("2026-11-02", -5m, "X", pending: false, id: "a") }, new[] { Ex(1, "2026-11-02", -5m, "X", pending: true, providerId: "a") });
        Assert.Equal(MatchKind.Reconcile, nowBooked[0].Kind);
        Assert.Equal(1, nowBooked[0].ExistingId);
    }

    [Fact]
    public void A_pending_operation_is_matched_when_the_booked_one_has_another_date_and_label()
    {
        var r = Reconciler.Match(new[] { In("2026-11-04", -15.99m, "AMAZON PAYMENTS", id: "b") },
            new[] { Ex(7, "2026-11-01", -15.99m, "AMAZON", pending: true, providerId: "a") });
        Assert.Equal(MatchKind.Reconcile, r[0].Kind);
        Assert.Equal(7, r[0].ExistingId);
    }

    [Fact]
    public void A_manual_entry_is_matched_on_amount_and_date_even_with_a_different_label()
    {
        var r = Reconciler.Match(new[] { In("2026-11-01", -86.25m, "SEPHORA NOYELLES") }, new[] { Ex(3, "2026-10-29", -86.25m, "cadeau", manual: true) });
        Assert.Equal(MatchKind.Reconcile, r[0].Kind);
    }

    [Fact]
    public void Amount_must_be_exact_and_dates_must_be_close()
    {
        var other = Reconciler.Match(new[] { In("2026-11-01", -86.26m, "SEPHORA") }, new[] { Ex(3, "2026-11-01", -86.25m, "SEPHORA", manual: true) });
        Assert.Equal(MatchKind.New, other[0].Kind);
        var far = Reconciler.Match(new[] { In("2026-11-20", -86.25m, "SEPHORA") }, new[] { Ex(3, "2026-11-01", -86.25m, "SEPHORA", manual: true) });
        Assert.Equal(MatchKind.New, far[0].Kind);
    }

    [Fact]
    public void A_booked_bank_line_only_matches_on_the_same_day_and_shop()
    {
        var sameDay = Reconciler.Match(new[] { In("2026-11-01", -4.99m, "AMAZON") }, new[] { Ex(1, "2026-11-01", -4.99m, "AMAZON PAYMENTS", providerId: "fp:1") });
        Assert.Equal(MatchKind.Duplicate, sameDay[0].Kind);
        // Un achat identique trois jours plus tard est un autre achat.
        var laterDay = Reconciler.Match(new[] { In("2026-11-04", -4.99m, "AMAZON") }, new[] { Ex(1, "2026-11-01", -4.99m, "AMAZON PAYMENTS", providerId: "fp:1") });
        Assert.Equal(MatchKind.New, laterDay[0].Kind);
    }

    [Fact]
    public void Each_existing_operation_is_used_once_and_ties_are_flagged_ambiguous()
    {
        var two = Reconciler.Match(
            new[] { In("2026-11-01", -4.99m, "AMAZON", id: "1"), In("2026-11-01", -4.99m, "AMAZON", id: "2") },
            new[] { Ex(1, "2026-11-01", -4.99m, "AMAZON", pending: true, providerId: "x") });
        Assert.Equal(MatchKind.Reconcile, two[0].Kind);
        Assert.Equal(MatchKind.New, two[1].Kind);

        var tie = Reconciler.Match(new[] { In("2026-11-01", -4.99m, "AMAZON") },
            new[] { Ex(1, "2026-11-01", -4.99m, "AMAZON", manual: true), Ex(2, "2026-11-01", -4.99m, "AMAZON", manual: true) });
        Assert.True(tie[0].Ambiguous);
    }

    [Fact]
    public void Merge_turns_a_manual_entry_into_the_bank_operation_and_keeps_the_note()
    {
        var manual = new Transaction { Id = 3, Amount = -86.25m, BookingDate = D("2026-10-29"), Note = "cadeau", CategoryId = "shop", CategorySource = CategorySource.Manual };
        Reconciler.Merge(manual, new ParsedTransaction(D("2026-11-01"), null, -86.25m, "SEPHORA", "détail", "paste:x", Pending: true), "paste:x");
        Assert.Equal(D("2026-11-01"), manual.BookingDate);
        Assert.Equal(TransactionStatus.Pending, manual.Status);
        Assert.Equal("paste:x", manual.ProviderTransactionId);
        Assert.Equal("SEPHORA", manual.Counterparty);
        Assert.Equal("cadeau", manual.Note);
        Assert.Equal("shop", manual.CategoryId);
    }
}
