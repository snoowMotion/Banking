using BudgetTenable.Core.Banking;
using BudgetTenable.Core.Import;

namespace BudgetTenable.Tests;

public class BankFeedTests
{
    private static EbTransaction Tx(string? reference, string? booking, string amount, string indicator = "DBIT", string status = "BOOK",
        string? creditor = "LECLERC", string? debtor = null, string? transactionDate = null, string? valueDate = null, params string[] remittance) =>
        new(reference, new EbAmount("EUR", amount), creditor is null ? null : new EbParty(creditor), debtor is null ? null : new EbParty(debtor),
            null, indicator, status, booking, valueDate, transactionDate, remittance.Length == 0 ? null : remittance.ToList(), null);

    [Fact]
    public void A_debit_is_negative_and_a_credit_positive_whatever_the_sign_sent_by_the_bank()
    {
        var debit = BankFeed.ToParsed(Tx("r1", "2026-10-29", "54.20"))!;
        Assert.Equal(-54.20m, debit.Amount);
        Assert.Equal("LECLERC", debit.Counterparty);
        Assert.Equal(new DateOnly(2026, 10, 29), debit.Date);
        Assert.False(debit.Pending);

        // Certaines banques envoient déjà le montant signé : seul l'indicateur fait foi.
        Assert.Equal(-54.20m, BankFeed.ToParsed(Tx("r1", "2026-10-29", "-54.20"))!.Amount);

        var credit = BankFeed.ToParsed(Tx("r2", "2026-10-29", "198.00", "CRDT", creditor: "MOI", debtor: "CAF DU NORD"))!;
        Assert.Equal(198.00m, credit.Amount);
        Assert.Equal("CAF DU NORD", credit.Counterparty);   // pour un crédit, la contrepartie est l'émetteur
    }

    [Fact]
    public void Pending_and_hold_are_pending_and_cancelled_rejected_or_scheduled_are_ignored()
    {
        Assert.True(BankFeed.ToParsed(Tx(null, null, "12.00", status: "PDNG", transactionDate: "2026-10-30"))!.Pending);
        Assert.True(BankFeed.ToParsed(Tx(null, null, "12.00", status: "HOLD", valueDate: "2026-10-30"))!.Pending);
        Assert.False(BankFeed.ToParsed(Tx("r", "2026-10-30", "12.00", status: "OTHR"))!.Pending);
        Assert.True(BankFeed.ToParsed(Tx("r", null, "12.00", status: "OTHR", transactionDate: "2026-10-30"))!.Pending);

        Assert.Null(BankFeed.ToParsed(Tx("r", "2026-10-30", "12.00", status: "CNCL")));
        Assert.Null(BankFeed.ToParsed(Tx("r", "2026-10-30", "12.00", status: "RJCT")));
        Assert.Null(BankFeed.ToParsed(Tx("r", "2026-11-15", "12.00", status: "SCHD")));
    }

    [Fact]
    public void An_unreadable_row_is_skipped_instead_of_failing_the_whole_sync()
    {
        Assert.Null(BankFeed.ToParsed(Tx("r", null, "12.00")));                 // aucune date
        Assert.Null(BankFeed.ToParsed(Tx("r", "2026-10-30", "douze")));         // montant illisible
        Assert.Null(BankFeed.ToParsed(Tx("r", "30/10/2026", "12.00")));         // date dans un autre format
    }

    [Fact]
    public void The_date_is_the_booking_date_then_the_transaction_date_then_the_value_date()
    {
        Assert.Equal(new DateOnly(2026, 10, 29), BankFeed.ToParsed(Tx("r", "2026-10-29", "1.00", transactionDate: "2026-10-27", valueDate: "2026-10-30"))!.Date);
        Assert.Equal(new DateOnly(2026, 10, 27), BankFeed.ToParsed(Tx("r", null, "1.00", status: "PDNG", transactionDate: "2026-10-27", valueDate: "2026-10-30"))!.Date);
        Assert.Equal(new DateOnly(2026, 10, 30), BankFeed.ToParsed(Tx("r", null, "1.00", status: "PDNG", valueDate: "2026-10-30T00:00:00Z"))!.Date);
    }

    [Fact]
    public void Remittance_lines_are_joined()
    {
        var t = BankFeed.ToParsed(Tx("r", "2026-10-29", "23.00", creditor: null, remittance: new[] { "PRLV SEPA", " ", "FREE MOBILE" }))!;
        Assert.Null(t.Counterparty);
        Assert.Equal("PRLV SEPA FREE MOBILE", t.Remittance);
    }

    [Fact]
    public void The_bank_reference_makes_the_identifier()
    {
        var ids = BankFeed.Assign(new[] { Tx("5561990681", "2026-10-29", "54.20") });
        Assert.Equal("eb:5561990681", Assert.Single(ids).Id);
        Assert.True(BankFeed.IsFromSync(ids[0].Id));
        Assert.False(BankFeed.IsFromSync("fp:abcdef"));
        Assert.False(BankFeed.IsFromSync(null));
    }

    [Fact]
    public void The_same_operation_returned_twice_by_overlapping_pages_is_kept_once()
    {
        var ids = BankFeed.Assign(new[] { Tx("A1", "2026-10-29", "54.20"), Tx("B2", "2026-10-29", "9.90"), Tx("A1", "2026-10-29", "54.20") });
        Assert.Equal(new[] { "eb:A1", "eb:B2" }, ids.Select(x => x.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Two_different_operations_sharing_a_reference_are_both_kept_with_stable_identifiers()
    {
        var a = Tx("DUP", "2026-10-29", "54.20");
        var b = Tx("DUP", "2026-10-30", "12.00", creditor: "BOULANGERIE");
        var first = BankFeed.Assign(new[] { a, b }).ToDictionary(x => x.Tx.Amount, x => x.Id);
        var second = BankFeed.Assign(new[] { b, a }).ToDictionary(x => x.Tx.Amount, x => x.Id);

        Assert.Equal(2, first.Count);
        Assert.NotEqual(first[-54.20m], first[-12.00m]);
        Assert.All(first.Values, id => Assert.StartsWith("eb:DUP", id));
        Assert.Equal(first, second);   // l'ordre renvoyé par la banque ne change pas les identifiants
    }

    [Fact]
    public void Without_a_reference_the_identifier_is_a_fingerprint_with_a_rank_for_identical_operations()
    {
        var coffee = Tx(null, "2026-10-29", "1.20", creditor: "CAFE");
        var ids = BankFeed.Assign(new[] { coffee, coffee }).Select(x => x.Id).ToArray();
        Assert.Equal(2, ids.Distinct().Count());
        Assert.All(ids, id => Assert.StartsWith("ebf:", id));
        Assert.Equal(ids, BankFeed.Assign(new[] { coffee, coffee }).Select(x => x.Id).ToArray());
    }

    [Fact]
    public void A_very_long_reference_is_hashed_to_fit_the_column()
    {
        var id = BankFeed.Assign(new[] { Tx(new string('x', 400), "2026-10-29", "1.00") })[0].Id;
        Assert.StartsWith("eb:#", id);
        Assert.True(id.Length <= 200);
    }

    [Fact]
    public void The_iban_is_never_kept_in_full()
    {
        Assert.Equal("FR•• •••• 0189", BankFeed.MaskIban("FR76 3000 6000 0112 3456 7890 189"));
        Assert.Null(BankFeed.MaskIban(null));
        Assert.Equal("••••", BankFeed.MaskIban("1234"));
    }

    [Fact]
    public void The_bank_sync_adopts_a_pasted_or_imported_operation_a_few_days_apart()
    {
        // L'opération collée porte la date affichée par le site ; l'API donne la date comptable, deux jours plus tard.
        var incoming = BankFeed.Assign(new[] { Tx("A1", "2026-10-29", "54.20") });
        var pasted = new ExistingCandidate(7, new DateOnly(2026, 10, 27), -54.20m, IsManual: false, IsPending: false, "fp:abc", "CB LECLERC", Adoptable: true);
        var match = Assert.Single(Reconciler.Match(incoming, new[] { pasted }));
        Assert.Equal(MatchKind.Reconcile, match.Kind);
        Assert.Equal(7, match.ExistingId);

        // Hors synchro (fichier, collage), une opération débitée ne se rapproche que le même jour : comportement inchangé.
        var strict = pasted with { Adoptable = false };
        Assert.Equal(MatchKind.New, Assert.Single(Reconciler.Match(incoming, new[] { strict })).Kind);

        // Une autre enseigne au même montant n'est pas adoptée.
        var other = pasted with { Text = "CB DECATHLON" };
        Assert.Equal(MatchKind.New, Assert.Single(Reconciler.Match(incoming, new[] { other })).Kind);
    }
}
