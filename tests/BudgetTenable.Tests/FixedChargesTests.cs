using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BudgetTenable.Core.Budget;
using BudgetTenable.Core.Domain;

namespace BudgetTenable.Tests;

public class FixedLinesTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    private static PeriodSummary Run(IEnumerable<SpendEntry> entries, BudgetSettings? s = null) =>
        PeriodSummaryCalculator.Calculate(s ?? BudgetDefaults.Settings(), BudgetDefaults.Categories(), entries.ToList(), PeriodKey.Parse("2026-10"), D("2026-11-02"));

    [Fact]
    public void The_seed_has_ten_fixed_charges_all_due_and_the_expected_amounts_sum_to_1396()
    {
        var lines = Run(Array.Empty<SpendEntry>()).FixedLines;
        Assert.Equal(10, lines.Count);
        Assert.All(lines, l => { Assert.Equal("due", l.Status); Assert.Equal(0, l.Count); });
        Assert.Equal(1396m, lines.Sum(l => l.Expected));
        Assert.DoesNotContain(lines, l => l.CategoryId is "tf" or "auto");     // provisions annuelles : suivies par les pots
    }

    [Fact]
    public void A_settled_credit_is_no_longer_expected()
    {
        var s = BudgetDefaults.Settings();
        s.ArkeaSettled = true;
        var lines = Run(Array.Empty<SpendEntry>(), s).FixedLines;
        Assert.DoesNotContain(lines, l => l.CategoryId == "arkea");
        Assert.Equal(1326m, lines.Sum(l => l.Expected));
    }

    [Fact]
    public void A_matching_payment_is_paid_and_a_different_amount_is_a_gap()
    {
        var lines = Run(new[]
        {
            new SpendEntry(D("2026-10-30"), "immo", -959m, 11, FromBank: true),
            new SpendEntry(D("2026-11-01"), "eau", -64.30m, 12),
        }).FixedLines.ToDictionary(l => l.CategoryId);

        Assert.Equal("paid", lines["immo"].Status);
        Assert.Equal(959m, lines["immo"].Paid);
        Assert.Equal(D("2026-10-30"), lines["immo"].LastDate);
        Assert.False(lines["immo"].AllManual);
        Assert.Equal(new[] { 11 }, lines["immo"].TransactionIds);

        Assert.Equal("gap", lines["eau"].Status);                               // 62 attendus, 64,30 payés
        Assert.Equal(64.30m, lines["eau"].Paid);
        Assert.True(lines["eau"].AllManual);
        Assert.Equal("due", lines["tel"].Status);
    }

    [Fact]
    public void A_half_euro_difference_still_counts_as_paid_and_installments_add_up()
    {
        var lines = Run(new[]
        {
            new SpendEntry(D("2026-10-30"), "tel", -91.60m, 1),
            new SpendEntry(D("2026-10-31"), "frais", -10m, 2),
            new SpendEntry(D("2026-11-01"), "frais", -12m, 3),
        }).FixedLines.ToDictionary(l => l.CategoryId);
        Assert.Equal("paid", lines["tel"].Status);
        Assert.Equal(22m, lines["frais"].Paid);
        Assert.Equal(2, lines["frais"].Count);
        Assert.Equal("paid", lines["frais"].Status);
    }

    [Fact]
    public void Fixed_charges_do_not_touch_the_variable_envelope_or_the_has_transactions_flag()
    {
        var s = Run(new[] { new SpendEntry(D("2026-11-01"), "immo", -959m, 1, true) });
        Assert.Equal(0m, s.VariableSpent);
        Assert.False(s.HasTransactions);
        Assert.All(s.DailyCumulative, v => Assert.Equal(0m, v));
        Assert.Equal(3m, s.FixedLines.Count(l => l.Status == "due") - 6m);      // 9 restent à payer, 1 payée
    }

    [Fact]
    public void Payments_outside_the_period_are_not_counted()
    {
        var lines = Run(new[] { new SpendEntry(D("2026-10-27"), "immo", -959m, 1), new SpendEntry(D("2026-11-28"), "immo", -959m, 2) }).FixedLines;
        Assert.Equal("due", lines.Single(l => l.CategoryId == "immo").Status);
    }
}

public class ApiFixedTests
{
    private static AppFactory Factory() => new() { Now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero) };

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement> Line(HttpClient c, string id, string period = "2026-10")
    {
        var s = await Json(await c.GetAsync("/api/periods/" + period));
        return s.GetProperty("fixedLines").EnumerateArray().Single(l => l.GetProperty("categoryId").GetString() == id);
    }

    private static Task<HttpResponseMessage> Pay(HttpClient c, string id, string period = "2026-10", object? body = null) =>
        c.PostAsJsonAsync($"/api/periods/{period}/fixed/{id}/paid", body ?? new { });

    [Fact]
    public async Task Clicking_a_charge_records_it_as_paid_and_clicking_again_cancels_it()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();

        Assert.Equal("due", (await Line(c, "immo")).GetProperty("status").GetString());
        await Json(await Pay(c, "immo"));

        var line = await Line(c, "immo");
        Assert.Equal("paid", line.GetProperty("status").GetString());
        Assert.Equal(959m, line.GetProperty("paid").GetDecimal());
        Assert.True(line.GetProperty("allManual").GetBoolean());

        var tx = (await Json(await c.GetAsync("/api/transactions?period=2026-10"))).GetProperty("items")[0];
        Assert.Equal(-959m, tx.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-11-02", tx.GetProperty("date").GetString());            // aujourd'hui, la période est en cours
        Assert.Equal("immo", tx.GetProperty("categoryId").GetString());
        Assert.Equal(1, tx.GetProperty("accountId").GetInt32());
        Assert.True(tx.GetProperty("isManual").GetBoolean());

        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, "immo")).StatusCode);    // pas de double marquage
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/periods/2026-10/fixed/immo/paid")).StatusCode);
        Assert.Equal("due", (await Line(c, "immo")).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/periods/2026-10/fixed/immo/paid")).StatusCode);
    }

    [Fact]
    public async Task The_charge_uses_its_own_debit_account_and_an_unknown_account_falls_back_to_the_default()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var b = await Json(await c.GetAsync("/api/budget"));

        // « Eau + EDF » est prélevée sur le compte commun (2) ; un compte inexistant (99) est ignoré.
        await Json(await c.PutAsJsonAsync("/api/budget", new
        {
            settings = b.GetProperty("settings"),
            categories = new object[] { new { id = "eau", accountId = 2 }, new { id = "tel", accountId = 99 } },
        }));
        var cats = (await Json(await c.GetAsync("/api/budget"))).GetProperty("categories").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
        Assert.Equal(2, cats["eau"].GetProperty("accountId").GetInt32());
        Assert.Equal(JsonValueKind.Null, cats["tel"].GetProperty("accountId").ValueKind);

        await Json(await Pay(c, "eau"));
        await Json(await Pay(c, "tel"));
        var items = (await Json(await c.GetAsync("/api/transactions?period=2026-10"))).GetProperty("items").EnumerateArray().ToDictionary(t => t.GetProperty("categoryId").GetString()!);
        Assert.Equal(2, items["eau"].GetProperty("accountId").GetInt32());
        Assert.Equal(1, items["tel"].GetProperty("accountId").GetInt32());

        // Compte choisi explicitement au clic : prioritaire.
        await Json(await Pay(c, "abo", body: new { accountId = 2 }));
        Assert.Equal(2, (await Json(await c.GetAsync("/api/transactions?period=2026-10&category=abo"))).GetProperty("items")[0].GetProperty("accountId").GetInt32());
    }

    [Theory]
    [InlineData("2026-09", "2026-10-27")]   // période passée : son dernier jour
    [InlineData("2026-12", "2026-12-28")]   // période à venir (nommée d'après son mois de début) : son premier jour
    public async Task The_default_date_follows_the_period(string period, string expected)
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Pay(c, "tel", period));
        var tx = (await Json(await c.GetAsync("/api/transactions?period=" + period))).GetProperty("items")[0];
        Assert.Equal(expected, tx.GetProperty("date").GetString());
    }

    [Fact]
    public async Task A_custom_amount_and_date_are_kept_and_the_gap_is_shown()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Pay(c, "eau", body: new { amount = 66.40, date = "2026-10-29" }));
        var line = await Line(c, "eau");
        Assert.Equal("gap", line.GetProperty("status").GetString());
        Assert.Equal(66.40m, line.GetProperty("paid").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "tel", body: new { date = "2026-12-15" })).StatusCode);   // hors période
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "tel", body: new { amount = -5 })).StatusCode);
    }

    [Fact]
    public async Task Only_active_fixed_charges_can_be_marked()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "courses")).StatusCode);      // catégorie variable
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "tf")).StatusCode);           // provision annuelle (pot)
        Assert.Equal(HttpStatusCode.NotFound, (await Pay(c, "inconnue")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "tel", "pas-une-periode")).StatusCode);

        var b = await Json(await c.GetAsync("/api/budget"));
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.GetProperty("settings").GetRawText())!;
        var arkea = new Dictionary<string, object?>(settings.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value))) { ["arkeaSettled"] = true };
        await Json(await c.PutAsJsonAsync("/api/budget", new { settings = arkea, categories = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, (await Pay(c, "arkea")).StatusCode);        // crédit soldé
        var s = await Json(await c.GetAsync("/api/periods/2026-10"));
        Assert.DoesNotContain(s.GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "arkea");
    }

    [Fact]
    public async Task A_charge_paid_through_the_bank_cannot_be_cancelled_by_a_click()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "sofiap", categoryId = "immo" }));
        await Json(await c.PostAsync("/api/import?accountId=1", new StringContent("Date;Libellé;Montant\n02/11/2026;PRLV SOFIAP;-959,00\n", Encoding.UTF8, "application/octet-stream")));

        var line = await Line(c, "immo");
        Assert.Equal("paid", line.GetProperty("status").GetString());
        Assert.False(line.GetProperty("allManual").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync("/api/periods/2026-10/fixed/immo/paid")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Pay(c, "immo")).StatusCode);
    }

    private static string Block(string label, string date, string method, decimal amount, string status = "En cours") =>
        $"{label}\n{date}\nPrêts et services\n{method}\n{status}\n{amount.ToString("0.00", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} €";

    [Fact]
    public async Task A_pasted_bank_line_reconciles_with_the_clicked_payment_without_duplicating_it()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Pay(c, "immo"));

        var p = await Json(await c.PostAsJsonAsync("/api/paste/preview", new { accountId = 1, text = Block("PRLV SOFIAP", "lun. 2 nov. à 08h00", "Prélèvement", -959m, "Comptabilisée") }));
        var item = p.GetProperty("items")[0];
        Assert.Equal("reconcile", item.GetProperty("kind").GetString());
        Assert.Equal("immo", item.GetProperty("suggestedCategoryId").GetString());
        Assert.Equal("existing", item.GetProperty("suggestionSource").GetString());

        await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            items = new[]
            {
                new
                {
                    date = "2026-11-02", label = "PRLV SOFIAP", detail = (string?)null, bankCategory = "Prêts et services", paymentMethod = "Prélèvement", pending = false,
                    amount = -959m, providerId = item.GetProperty("providerId").GetString(), action = "reconcile",
                    reconcileId = item.GetProperty("existing").GetProperty("id").GetInt32(), categoryId = "immo", newCategoryKey = (string?)null, @internal = false,
                },
            },
        }));

        Assert.Equal(1, (await Json(await c.GetAsync("/api/transactions?limit=10"))).GetProperty("total").GetInt32());
        var line = await Line(c, "immo");
        Assert.Equal("paid", line.GetProperty("status").GetString());
        Assert.False(line.GetProperty("allManual").GetBoolean());                  // devenue une opération de la banque
        Assert.Equal(1, line.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task A_pasted_line_is_suggested_a_fixed_charge_only_on_a_unique_amount_with_a_matching_label_or_a_direct_debit()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        string Text(params (string label, decimal amount, string method)[] rows) =>
            string.Join("\n", rows.Select((r, i) => Block(r.label, $"lun. 2 nov. à 1{i}h00", r.method, r.amount)));
        async Task<JsonElement[]> Sources(string text) =>
            (await Json(await c.PostAsJsonAsync("/api/paste/preview", new { accountId = 1, text }))).GetProperty("items").EnumerateArray().ToArray();

        var items = await Sources(Text(
            ("FREE TELECOM", -92m, "Carte bancaire"),     // libellé proche de « Télécom (Free box + mobile) »
            ("MYSTERE", -23m, "Prélèvement"),             // prélèvement de 23 € : « abonnements » ET « Anthropic », donc ambigu
            ("ANTHROPIC CLAUDE", -23m, "Carte bancaire"), // 23 € et libellé proche : une seule candidate
            ("LOYER NOZ", -10m, "Carte bancaire"),        // montant rond sans rapport avec « Ring »
            ("INCONNU", -62m, "Prélèvement"),             // prélèvement de 62 € : « Eau + EDF » est la seule candidate
            ("REMBOURSEMENT", 92m, "Virement")));         // un crédit n'est pas une charge
        Assert.Equal(new[] { "tel", null, "anth", null, "eau", null }, items.Select(i => i.GetProperty("suggestionSource").GetString() == "fixed" ? i.GetProperty("suggestedCategoryId").GetString() : null));

        // Une seule des deux opérations reçoit la proposition.
        var twice = await Sources(Text(("A", -92m, "Prélèvement"), ("B", -92m, "Prélèvement")));
        Assert.Equal(new[] { "fixed", "none" }, twice.Select(i => i.GetProperty("suggestionSource").GetString()));

        // Une fois la charge payée par un clic, une ligne de même montant se rapproche de ce paiement : plus de proposition « fixed ».
        await Json(await Pay(c, "tel"));
        var after = (await Sources(Text(("AUTRE", -92m, "Prélèvement"))))[0];
        Assert.Equal("reconcile", after.GetProperty("kind").GetString());
        Assert.Equal("existing", after.GetProperty("suggestionSource").GetString());
    }
}
