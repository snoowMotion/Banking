using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace BudgetTenable.Tests;

public class ApiBudgetHistoryTests
{
    // 2 novembre 2026 : la période en cours est celle du 28 octobre (2026-10).
    private static AppFactory Factory(string? dir = null) => new(dir) { Now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero) };

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<JsonElement> Budget(HttpClient c, string? period = null) => c.GetAsync("/api/budget" + (period is null ? "" : "?period=" + period)).ContinueWith(t => Json(t.Result)).Unwrap();
    private static JsonElement Cat(JsonElement budget, string id) => budget.GetProperty("categories").EnumerateArray().Single(x => x.GetProperty("id").GetString() == id);
    private static Task<JsonElement> Summary(HttpClient c, string period) => c.GetAsync("/api/periods/" + period).ContinueWith(t => Json(t.Result)).Unwrap();
    private static JsonElement Fixed(JsonElement summary, string id) => summary.GetProperty("fixedLines").EnumerateArray().Single(x => x.GetProperty("categoryId").GetString() == id);

    /// <summary>Enregistre le budget affiché à une période avec quelques changements, comme le fait le navigateur.</summary>
    private static async Task<HttpResponseMessage> Save(HttpClient c, string? period, object[]? cats = null, object? income = null, decimal bonus = 3000, bool propagate = false)
    {
        var b = await Budget(c, period);
        var s = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.GetProperty("settings").GetRawText())!;
        var settings = new Dictionary<string, object?>(s.Select(kv => new KeyValuePair<string, object?>(kv.Key, kv.Value)));
        if (income is not null) settings["income"] = income;
        settings["annualBonus"] = bonus;
        return await c.PutAsJsonAsync("/api/budget", new { period, propagate, settings, categories = cats ?? Array.Empty<object>() });
    }

    [Fact]
    public async Task A_change_made_in_the_current_period_counts_this_month_and_the_next_ones_but_not_the_past()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();

        await Json(await Save(c, null, new object[] { new { id = "tel", amount = 100 } }));          // période en cours par défaut

        Assert.Equal(100m, Fixed(await Summary(c, "2026-10"), "tel").GetProperty("expected").GetDecimal());   // ce mois-ci
        Assert.Equal(100m, Fixed(await Summary(c, "2026-12"), "tel").GetProperty("expected").GetDecimal());   // la suite
        Assert.Equal(92m, Fixed(await Summary(c, "2026-09"), "tel").GetProperty("expected").GetDecimal());    // l'historique ne bouge pas
        Assert.Equal(92m, Cat(await Budget(c, "2026-08"), "tel").GetProperty("amount").GetDecimal());
        Assert.Equal(100m, Cat(await Budget(c), "tel").GetProperty("amount").GetDecimal());

        // L'enveloppe de la période en cours baisse de 8 €, celle de septembre est intacte.
        var cur = await Summary(c, "2026-10");
        var past = await Summary(c, "2026-09");
        Assert.True(Math.Abs(past.GetProperty("variableBudget").GetDecimal() - cur.GetProperty("variableBudget").GetDecimal() - 0.76m * 8m) < 0.01m);
    }

    [Fact]
    public async Task Income_changes_are_dated_too_and_a_past_correction_stops_at_the_next_dated_change()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Save(c, "2026-11", income: new { net = 2200, partner = 750, caf = 198 }));       // augmentation à partir de novembre
        Assert.Equal(2998m, (await Summary(c, "2026-10")).GetProperty("income").GetDecimal());
        Assert.Equal(3148m, (await Summary(c, "2026-11")).GetProperty("income").GetDecimal());

        // Correction d'une période passée : vaut de là jusqu'au changement daté suivant (novembre).
        await Json(await Save(c, "2026-08", income: new { net = 2100, partner = 750, caf = 198 }));
        Assert.Equal(2998m, (await Summary(c, "2026-07")).GetProperty("income").GetDecimal());
        Assert.Equal(3048m, (await Summary(c, "2026-09")).GetProperty("income").GetDecimal());
        Assert.Equal(3148m, (await Summary(c, "2026-11")).GetProperty("income").GetDecimal());
    }

    [Fact]
    public async Task Later_changes_are_reported_and_can_be_overwritten_with_propagate()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Save(c, "2026-12", new object[] { new { id = "tel", amount = 120 } }));          // changement déjà prévu en décembre

        await Json(await Save(c, "2026-10", new object[] { new { id = "tel", amount = 100 } }));
        var b = await Budget(c, "2026-10");
        var later = b.GetProperty("laterChanges").EnumerateArray().ToArray();
        Assert.Contains(later, l => l.GetProperty("categoryId").GetString() == "tel" && l.GetProperty("from").GetString() == "2026-12");
        Assert.Equal(120m, Fixed(await Summary(c, "2026-12"), "tel").GetProperty("expected").GetDecimal());   // non écrasé

        await Json(await Save(c, "2026-10", new object[] { new { id = "tel", amount = 105 } }, propagate: true));
        Assert.Equal(105m, Fixed(await Summary(c, "2026-12"), "tel").GetProperty("expected").GetDecimal());   // propagé
        Assert.Empty((await Budget(c, "2026-10")).GetProperty("laterChanges").EnumerateArray());
    }

    [Fact]
    public async Task Putting_back_the_previous_value_removes_the_dated_change()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Save(c, "2026-11", new object[] { new { id = "tel", amount = 100 } }));
        Assert.Single((await Budget(c, "2026-10")).GetProperty("laterChanges").EnumerateArray());
        await Json(await Save(c, "2026-11", new object[] { new { id = "tel", amount = 92 } }));
        Assert.Empty((await Budget(c, "2026-10")).GetProperty("laterChanges").EnumerateArray());
    }

    [Fact]
    public async Task A_new_fixed_charge_exists_from_the_current_period_and_shows_its_impact()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var before = await Summary(c, "2026-10");

        var created = await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Cantine", group = "fixe", amount = 45, accountId = 2 }));
        Assert.Equal("cantine", created.GetProperty("id").GetString());
        Assert.Equal("2026-10", created.GetProperty("activeFrom").GetString());

        var after = await Summary(c, "2026-10");
        Assert.Equal(45m, Fixed(after, "cantine").GetProperty("expected").GetDecimal());
        Assert.Equal(2, Fixed(after, "cantine").GetProperty("accountId").GetInt32());
        Assert.Equal(before.GetProperty("fixedMonthly").GetDecimal() + 45m, after.GetProperty("fixedMonthly").GetDecimal());
        Assert.True(after.GetProperty("variableBudget").GetDecimal() < before.GetProperty("variableBudget").GetDecimal());

        // Pas dans les périodes d'avant : le budget passé est celui qu'on avait.
        var past = await Summary(c, "2026-09");
        Assert.DoesNotContain(past.GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "cantine");
        Assert.False(Cat(await Budget(c, "2026-09"), "cantine").GetProperty("active").GetBoolean());
        Assert.True(Cat(await Budget(c), "cantine").GetProperty("activeNow").GetBoolean());

        // On peut la marquer payée dans sa période, pas avant.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/periods/2026-10/fixed/cantine/paid", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/periods/2026-09/fixed/cantine/paid", new { })).StatusCode);
    }

    [Fact]
    public async Task A_new_variable_category_takes_its_share_from_the_savings()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var before = await Summary(c, "2026-10");
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Sport", group = "variable", percentage = 5 }));
        var after = await Summary(c, "2026-10");

        Assert.Equal(before.GetProperty("variableBudget").GetDecimal() + before.GetProperty("savingsTarget").GetDecimal() * 5m / 12m, after.GetProperty("variableBudget").GetDecimal());
        Assert.True(after.GetProperty("savingsTarget").GetDecimal() < before.GetProperty("savingsTarget").GetDecimal());
        Assert.Contains(after.GetProperty("envelopes").EnumerateArray(), e => e.GetProperty("categoryId").GetString() == "sport");
        Assert.Equal(before.GetProperty("savingsTarget").GetDecimal() * 7m / 12m, after.GetProperty("savingsTarget").GetDecimal());   // 12 % -> 7 %
    }

    [Fact]
    public async Task Pots_and_annual_provisions_can_be_created_and_their_opening_balance_is_kept()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Santé", group = "pot", percentage = 2, opening = 80 }));
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Redevance", group = "pot", annual = true, amount = 138 }));
        var s = await Summary(c, "2026-10");
        var pots = s.GetProperty("pots").EnumerateArray().ToDictionary(p => p.GetProperty("categoryId").GetString()!);
        Assert.Equal(80m, pots["sante"].GetProperty("balance").GetDecimal());                      // avant la première période de virement
        Assert.Equal(138m / 12m, pots["redevance"].GetProperty("monthly").GetDecimal());
        Assert.Equal(80m, (await Budget(c)).GetProperty("settings").GetProperty("potOpening").GetProperty("sante").GetDecimal());
    }

    [Fact]
    public async Task Pot_balances_do_not_move_when_a_percentage_changes_later()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var b = await Budget(c);
        var settings = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(b.GetProperty("settings").GetRawText())!;
        settings["potStart"] = JsonDocument.Parse("\"2026-08\"").RootElement;
        await Json(await c.PutAsJsonAsync("/api/budget", new { period = "2026-10", settings, categories = Array.Empty<object>() }));

        decimal Voiture(JsonElement s) => s.GetProperty("pots").EnumerateArray().Single(p => p.GetProperty("categoryId").GetString() == "voiture").GetProperty("balance").GetDecimal();
        var aug = Voiture(await Summary(c, "2026-08"));
        var oct = Voiture(await Summary(c, "2026-10"));
        await Json(await Save(c, "2026-11", new object[] { new { id = "voiture", percentage = 12 } }));
        Assert.Equal(aug, Voiture(await Summary(c, "2026-08")));
        Assert.Equal(oct, Voiture(await Summary(c, "2026-10")));
        Assert.True(Voiture(await Summary(c, "2026-11")) > oct);
    }

    [Fact]
    public async Task Creation_is_validated()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        Task<HttpResponseMessage> Post(object o) => c.PostAsJsonAsync("/api/categories", o);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "  ", group = "fixe" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "A", group = "variable", percentage = 101 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "B", group = "variable", percentage = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "C", group = "fixe", annual = true, amount = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "D", group = "pot", annual = true, amount = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "E", group = "fixe", amount = 5, accountId = 99 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "F", group = "fixe", amount = 5, from = "bientôt" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(new { label = "G", group = "n-importe-quoi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(new { label = "  courses ET maison ", group = "variable" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Post(new { label = "Ok", group = "ignore" })).StatusCode);
    }

    [Fact]
    public async Task A_stopped_charge_keeps_its_history_and_can_be_restored_later()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/categories/tel/archive", new { from = "2026-11" })).StatusCode);

        Assert.Contains(Summary(c, "2026-10").Result.GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "tel");
        Assert.DoesNotContain((await Summary(c, "2026-11")).GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "tel");
        var b = await Budget(c, "2026-12");
        Assert.False(Cat(b, "tel").GetProperty("active").GetBoolean());
        Assert.Equal("2026-11", Cat(b, "tel").GetProperty("endedFrom").GetString());
        Assert.True((await Summary(c, "2026-11")).GetProperty("fixedMonthly").GetDecimal() < (await Summary(c, "2026-10")).GetProperty("fixedMonthly").GetDecimal());

        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/categories/tel/archive", new { from = "2027-01" })).StatusCode);   // déjà arrêtée
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/categories/tel/restore", new { from = "2027-03" })).StatusCode);
        Assert.DoesNotContain((await Summary(c, "2027-02")).GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "tel");
        Assert.Equal(92m, Fixed(await Summary(c, "2027-03"), "tel").GetProperty("expected").GetDecimal());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/categories/tel/restore", new { from = "2027-04" })).StatusCode);   // déjà active
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/categories/inconnue/archive", new { })).StatusCode);
    }

    [Fact]
    public async Task Spending_in_a_category_stopped_or_not_yet_created_is_still_counted()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Sport", group = "variable", percentage = 3, from = "2026-12" }));
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -40, categoryId = "sport", accountId = 1 }));
        var s = await Summary(c, "2026-10");
        Assert.Equal(40m, s.GetProperty("variableSpent").GetDecimal());
        Assert.Contains(s.GetProperty("envelopes").EnumerateArray(), e => e.GetProperty("categoryId").GetString() == "__inactive" && e.GetProperty("spent").GetDecimal() == 40m);
    }

    [Fact]
    public async Task Merging_moves_operations_rules_and_bank_mappings_and_removes_the_source()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Télécoms", group = "variable" }));      // le doublon de « Télécom (Free box + mobile) »
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -92, categoryId = "telecoms", accountId = 1 }));
        await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "free", categoryId = "telecoms" }));
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            mappings = new[] { new { bankCategory = "Télécoms", categoryId = "telecoms", ignore = false } },
            items = new[] { new { date = "2026-11-01", label = "X", amount = -1m, providerId = "paste:x", action = "skip", @internal = false, pending = false } },
        }));

        var merged = await Json(await c.PostAsJsonAsync("/api/categories/telecoms/merge", new { intoId = "tel" }));
        Assert.Equal(1, merged.GetProperty("moved").GetInt32());
        Assert.Equal(1, merged.GetProperty("rules").GetInt32());

        Assert.Equal("tel", (await Json(await c.GetAsync("/api/transactions?limit=5"))).GetProperty("items")[0].GetProperty("categoryId").GetString());
        Assert.Equal("tel", (await Json(await c.GetAsync("/api/rules")))[0].GetProperty("categoryId").GetString());
        Assert.Equal("tel", (await Json(await c.GetAsync("/api/bank-categories")))[0].GetProperty("categoryId").GetString());
        Assert.DoesNotContain((await Budget(c)).GetProperty("categories").EnumerateArray(), x => x.GetProperty("id").GetString() == "telecoms");
        Assert.Equal("paid", Fixed(await Summary(c, "2026-10"), "tel").GetProperty("status").GetString());           // la dépense compte maintenant dans la charge fixe

        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/categories/telecoms/merge", new { intoId = "tel" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/categories/tel/merge", new { intoId = "tel" })).StatusCode);
    }

    [Fact]
    public async Task A_pot_balance_follows_the_pot_it_is_merged_into()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Cadeaux", group = "pot", percentage = 1, opening = 60 }));
        await Json(await c.PostAsJsonAsync("/api/categories/cadeaux/merge", new { intoId = "vac" }));
        Assert.Equal(60m, (await Budget(c)).GetProperty("settings").GetProperty("potOpening").GetProperty("vac").GetDecimal());
    }

    [Fact]
    public async Task Only_unused_categories_can_be_deleted_and_rename_checks_names()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Essai", group = "variable" }));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/categories/essai")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/categories/essai")).StatusCode);

        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -5, categoryId = "shop", accountId = 1 }));
        var refused = await c.DeleteAsync("/api/categories/shop");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("1 opération", await refused.Content.ReadAsStringAsync());
        Assert.Equal(1, Cat(await Budget(c), "shop").GetProperty("usedBy").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await c.PutAsJsonAsync("/api/categories/shop", new { label = "Achats", note = "tout et rien" })).StatusCode);
        Assert.Equal("Achats", Cat(await Budget(c), "shop").GetProperty("label").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync("/api/categories/shop", new { label = "courses et maison" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/categories/shop", new { label = " " })).StatusCode);
    }

    [Fact]
    public async Task Reset_goes_back_to_the_seed_values_from_a_period_without_touching_the_past()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Save(c, "2026-10", new object[] { new { id = "tel", amount = 120 }, new { id = "shop", percentage = 20 } }, income: new { net = 2500, partner = 750, caf = 198 }));
        await Json(await c.PostAsJsonAsync("/api/budget/reset", new { period = "2026-11" }));

        var cur = await Budget(c, "2026-11");
        Assert.Equal(92m, Cat(cur, "tel").GetProperty("amount").GetDecimal());
        Assert.Equal(10m, Cat(cur, "shop").GetProperty("percentage").GetDecimal());
        Assert.Equal(2050m, cur.GetProperty("settings").GetProperty("income").GetProperty("net").GetDecimal());
        Assert.Equal(120m, Cat(await Budget(c, "2026-10"), "tel").GetProperty("amount").GetDecimal());           // octobre garde ses valeurs modifiées
    }

    [Fact]
    public async Task Paste_can_create_a_fixed_charge_with_its_amount_from_the_period_of_the_oldest_operation()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var r = await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            newCategories = new[] { new { key = "cantine", label = "Cantine", group = "fixe", amount = 45, percentage = (decimal?)null, accountId = 2 } },
            items = new[]
            {
                new { date = "2026-10-30", label = "MAIRIE", amount = -45m, providerId = "paste:a", action = "add", @internal = false, pending = false, newCategoryKey = "cantine" },
            },
        }));
        Assert.Equal(1, r.GetProperty("categoriesCreated").GetInt32());
        var cat = Cat(await Budget(c), "cantine");
        Assert.Equal("fixe", cat.GetProperty("group").GetString());
        Assert.Equal(45m, cat.GetProperty("amount").GetDecimal());
        Assert.Equal("2026-10", cat.GetProperty("activeFrom").GetString());
        Assert.Equal("paid", Fixed(await Summary(c, "2026-10"), "cantine").GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_old_settled_flag_becomes_a_dated_stop_that_keeps_the_history()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bt-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = Factory(dir)) { _ = await first.LoggedInAsync(); }          // crée la base
            SqliteConnection.ClearAllPools();
            using (var db = new SqliteConnection($"Data Source={Path.Combine(dir, "budget.db")}"))
            {
                db.Open();
                var cmd = db.CreateCommand();
                cmd.CommandText = "UPDATE AppSettings SET Json = '{\"income\":{\"net\":2050,\"partner\":750,\"caf\":198},\"annualBonus\":3000,\"includeBonus\":true,\"arkeaSettled\":true,\"startDay\":28,\"potStart\":\"2026-11\",\"potOpening\":{},\"lddsDone\":{}}' WHERE Id = 1";
                cmd.ExecuteNonQuery();
            }
            SqliteConnection.ClearAllPools();

            using var second = Factory(dir);
            var c = await second.LoggedInAsync();
            Assert.DoesNotContain((await Summary(c, "2026-10")).GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "arkea");
            Assert.Contains((await Summary(c, "2026-09")).GetProperty("fixedLines").EnumerateArray(), l => l.GetProperty("categoryId").GetString() == "arkea");
            Assert.False(Cat(await Budget(c), "arkea").GetProperty("active").GetBoolean());
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task History_bars_carry_each_periods_own_budget()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await Save(c, "2026-10", new object[] { new { id = "assur", amount = 175 } }));       // -100 € de budget à partir d'octobre
        var s = await Summary(c, "2026-10");
        var hist = s.GetProperty("history").EnumerateArray().ToDictionary(h => h.GetProperty("key").GetString()!);
        Assert.True(hist["2026-09"].GetProperty("budget").GetDecimal() > hist["2026-10"].GetProperty("budget").GetDecimal());
        Assert.Equal(s.GetProperty("variableBudget").GetDecimal(), hist["2026-10"].GetProperty("budget").GetDecimal());
    }
}
