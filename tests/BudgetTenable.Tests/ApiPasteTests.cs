using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace BudgetTenable.Tests;

public class ApiPasteTests
{
    private static AppFactory Factory() => new() { Now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero) };

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> Preview(HttpClient c, string text, int account = 1) =>
        c.PostAsJsonAsync("/api/paste/preview", new { text, accountId = account });

    private static JsonElement[] Items(JsonElement preview) => preview.GetProperty("items").EnumerateArray().ToArray();

    /// <summary>Transforme un élément de l'aperçu en élément de validation, comme le fait le navigateur.</summary>
    private static object Commit(JsonElement item, string action = "add", string? categoryId = null, string? newKey = null, int? reconcileId = null, bool? pending = null) => new
    {
        date = item.GetProperty("date").GetString(),
        label = item.GetProperty("label").GetString(),
        detail = item.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
        bankCategory = item.GetProperty("bankCategory").GetString(),
        paymentMethod = item.GetProperty("paymentMethod").GetString(),
        pending = pending ?? item.GetProperty("pending").GetBoolean(),
        amount = item.GetProperty("amount").GetDecimal(),
        providerId = item.GetProperty("providerId").GetString(),
        action,
        reconcileId,
        categoryId,
        newCategoryKey = newKey,
        @internal = false,
    };

    [Fact]
    public async Task Preview_reads_the_example_and_knows_nothing_about_the_bank_categories_yet()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var p = await Json(await Preview(c, PastedStatementParserTests.Sample));

        var items = Items(p);
        Assert.Equal(5, items.Length);
        Assert.All(items, i => Assert.Equal("new", i.GetProperty("kind").GetString()));
        Assert.All(items, i => Assert.Equal("none", i.GetProperty("suggestionSource").GetString()));
        Assert.Equal("ANTHROPIC CLAUDE SUB", items[0].GetProperty("label").GetString());
        Assert.Equal("2026-11-02", items[0].GetProperty("date").GetString());
        Assert.Equal("10:33", items[0].GetProperty("time").GetString());
        Assert.True(items[0].GetProperty("pending").GetBoolean());

        var bank = p.GetProperty("bankCategories").EnumerateArray().ToDictionary(b => b.GetProperty("label").GetString()!);
        Assert.Equal(4, bank.Count);                                                   // « Shopping et services - autre » apparaît deux fois
        Assert.Equal(2, bank["Shopping et services - autre"].GetProperty("count").GetInt32());
        Assert.All(bank.Values, b => Assert.False(b.GetProperty("known").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, bank["Shopping et services - autre"].GetProperty("matchingCategoryId").ValueKind);
    }

    [Fact]
    public async Task Committing_creates_the_missing_category_remembers_the_mapping_and_reuses_it_next_time()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var items = Items(await Json(await Preview(c, PastedStatementParserTests.Sample)));

        var commit = await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            newCategories = new[] { new { key = "coiffeurs", label = "Coiffeurs et soins du corps", group = "variable" } },
            mappings = new object[]
            {
                new { bankCategory = "Coiffeurs et soins du corps", newCategoryKey = "coiffeurs", ignore = false },
                new { bankCategory = "Shopping et services - autre", categoryId = "shop", ignore = false },
                new { bankCategory = "Loyers et traites", ignore = true },          // NOZ : la banque se trompe, on n'utilise pas cette catégorie
            },
            items = new[]
            {
                Commit(items[0]),                                                   // Anthropic : catégorie bancaire non associée -> à trier
                Commit(items[1], newKey: "coiffeurs"),
                Commit(items[2], categoryId: "shop"),
                Commit(items[3], categoryId: "shop"),
                Commit(items[4], categoryId: "divers"),
            },
        }));
        Assert.Equal(5, commit.GetProperty("added").GetInt32());
        Assert.Equal(1, commit.GetProperty("categoriesCreated").GetInt32());
        Assert.Equal(3, commit.GetProperty("mappingsSaved").GetInt32());
        Assert.Equal(1, commit.GetProperty("toSort").GetInt32());

        var budget = await Json(await c.GetAsync("/api/budget"));
        var created = budget.GetProperty("categories").EnumerateArray().Single(x => x.GetProperty("label").GetString() == "Coiffeurs et soins du corps");
        Assert.Equal("coiffeurs-et-soins-du-corps", created.GetProperty("id").GetString());
        Assert.Equal("variable", created.GetProperty("group").GetString());
        Assert.Equal(0m, created.GetProperty("percentage").GetDecimal());           // sans budget tant qu'on ne l'a pas réglé

        // Les cinq opérations (du 31 oct. au 2 nov.) tombent dans la période qui commence le 28 octobre.
        Assert.Equal(5, (await Json(await c.GetAsync("/api/transactions?period=2026-10&limit=50"))).GetProperty("total").GetInt32());
        Assert.Equal(0, (await Json(await c.GetAsync("/api/transactions?period=2026-09&limit=50"))).GetProperty("total").GetInt32());
        var unsorted = await Json(await c.GetAsync("/api/transactions?period=2026-10&unsorted=true"));
        Assert.Equal("ANTHROPIC CLAUDE SUB", unsorted.GetProperty("items")[0].GetProperty("counterparty").GetString());
        var all = await Json(await c.GetAsync("/api/transactions?limit=50"));
        Assert.All(all.GetProperty("items").EnumerateArray(), t => Assert.Equal("pending", t.GetProperty("status").GetString()));
        Assert.Equal(5, all.GetProperty("total").GetInt32());

        // Les mêmes opérations recollées ne s'ajoutent pas une seconde fois.
        var again = Items(await Json(await Preview(c, PastedStatementParserTests.Sample)));
        Assert.All(again, i => Assert.Equal("duplicate", i.GetProperty("kind").GetString()));

        // Un nouveau collage retrouve les correspondances retenues.
        const string next = "ZARA\nmar. 3 nov. à 11h00\nShopping et services - autre\nCarte bancaire\nEn cours\n-39,90 €\nLOYER NOZ\nmar. 3 nov. à 12h00\nLoyers et traites\nCarte bancaire\nEn cours\n-10,00 €";
        var p2 = await Json(await Preview(c, next));
        var i2 = Items(p2);
        Assert.Equal("shop", i2[0].GetProperty("suggestedCategoryId").GetString());
        Assert.Equal("mapping", i2[0].GetProperty("suggestionSource").GetString());
        Assert.Equal("none", i2[1].GetProperty("suggestionSource").GetString());      // « ne pas utiliser » est retenu aussi
        Assert.All(p2.GetProperty("bankCategories").EnumerateArray(), b => Assert.True(b.GetProperty("known").GetBoolean()));
        Assert.True(p2.GetProperty("bankCategories").EnumerateArray().Single(b => b.GetProperty("label").GetString() == "Loyers et traites").GetProperty("ignored").GetBoolean());
    }

    [Fact]
    public async Task A_user_rule_beats_the_bank_category_mapping()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            mappings = new[] { new { bankCategory = "Loyers et traites", categoryId = "divers", ignore = false } },
            items = new[] { new { date = "2026-11-01", label = "X", amount = -1m, providerId = "paste:x", action = "skip", @internal = false, pending = false } },
        }));
        await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "noz", categoryId = "shop" }));
        var p = await Json(await Preview(c, "NOZ\nsam. 31 oct. à 16h31 - COURRIERES\nLoyers et traites\nCarte bancaire\nEn cours\n-28,99 €"));
        var item = Items(p)[0];
        Assert.Equal("shop", item.GetProperty("suggestedCategoryId").GetString());
        Assert.Equal("rule", item.GetProperty("suggestionSource").GetString());

        // Validée telle quelle, l'opération est classée par la règle (donc ré-évaluable), pas figée à la main.
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = new[] { Commit(item, categoryId: "shop") } }));
        var t = (await Json(await c.GetAsync("/api/transactions?limit=5"))).GetProperty("items")[0];
        Assert.Equal("rule", t.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_manual_entry_is_reconciled_with_the_pasted_operation_and_keeps_its_note_and_category()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var manual = await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -86.25, categoryId = "shop", note = "cadeau", accountId = 1 }));

        var items = Items(await Json(await Preview(c, PastedStatementParserTests.Sample)));
        var sephora = items[1];
        Assert.Equal("reconcile", sephora.GetProperty("kind").GetString());
        Assert.Equal(manual.GetProperty("id").GetInt32(), sephora.GetProperty("existing").GetProperty("id").GetInt32());
        Assert.Equal("shop", sephora.GetProperty("suggestedCategoryId").GetString());       // la catégorie déjà posée est proposée
        Assert.Equal("existing", sephora.GetProperty("suggestionSource").GetString());

        var result = await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            items = new[] { Commit(sephora, "reconcile", categoryId: "shop", reconcileId: manual.GetProperty("id").GetInt32()) },
        }));
        Assert.Equal(1, result.GetProperty("reconciled").GetInt32());
        Assert.Equal(0, result.GetProperty("added").GetInt32());

        var all = await Json(await c.GetAsync("/api/transactions?limit=10"));
        Assert.Equal(1, all.GetProperty("total").GetInt32());                               // pas de doublon
        var t = all.GetProperty("items")[0];
        Assert.Equal("cadeau", t.GetProperty("note").GetString());
        Assert.Equal("shop", t.GetProperty("categoryId").GetString());
        Assert.Equal("2026-11-01", t.GetProperty("date").GetString());
        Assert.Equal("pending", t.GetProperty("status").GetString());
        Assert.False(t.GetProperty("isManual").GetBoolean());                               // devenue une opération bancaire
        Assert.Equal("SEPHORA NOYELLES GODAULT", t.GetProperty("counterparty").GetString());
    }

    [Fact]
    public async Task A_pending_operation_that_is_now_booked_is_updated_not_duplicated()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        const string pending = "AMAZON\ndim. 1 nov. à 15h20 - Achat en ligne\nShopping et services - autre\nCarte bancaire\nEn cours\n-15,99 €";
        var first = Items(await Json(await Preview(c, pending)))[0];
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = new[] { Commit(first, categoryId: "shop") } }));

        // Même date et même heure, statut changé : même identifiant, donc simple mise à jour du statut.
        const string booked = "AMAZON\ndim. 1 nov. à 15h20 - Achat en ligne\nShopping et services - autre\nCarte bancaire\nComptabilisée\n-15,99 €";
        var second = Items(await Json(await Preview(c, booked)))[0];
        Assert.Equal("reconcile", second.GetProperty("kind").GetString());
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            items = new[] { Commit(second, "reconcile", reconcileId: second.GetProperty("existing").GetProperty("id").GetInt32()) },
        }));
        var all = await Json(await c.GetAsync("/api/transactions?limit=10"));
        Assert.Equal(1, all.GetProperty("total").GetInt32());
        Assert.Equal("booked", all.GetProperty("items")[0].GetProperty("status").GetString());

        // Et quand la banque change aussi la date et le libellé : rapprochement par montant, date proche et enseigne.
        const string moved = "AMAZON PAYMENTS\nmar. 3 nov. à 02h00\nShopping et services - autre\nCarte bancaire\nComptabilisée\n-15,99 €";
        var third = Items(await Json(await Preview(c, moved)))[0];
        // Une opération déjà débitée ne se rapproche que le même jour : le même montant deux jours plus tard est un autre achat.
        Assert.Equal("new", third.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Pending_operation_is_followed_when_the_booked_line_has_another_date()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        const string pending = "AMAZON\ndim. 1 nov. à 15h20 - Achat en ligne\nShopping\nCarte bancaire\nEn cours\n-15,99 €";
        var first = Items(await Json(await Preview(c, pending)))[0];
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = new[] { Commit(first, categoryId: "shop") } }));

        const string moved = "AMAZON PAYMENTS\nmar. 3 nov. à 02h00\nShopping\nCarte bancaire\nComptabilisée\n-15,99 €";
        var next = Items(await Json(await Preview(c, moved)))[0];
        Assert.Equal("reconcile", next.GetProperty("kind").GetString());
        Assert.False(next.GetProperty("ambiguous").GetBoolean());
    }

    [Fact]
    public async Task A_csv_import_reconciles_a_manual_entry_instead_of_duplicating_it()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-29", amount = -85.40, categoryId = "courses", note = "Leclerc", accountId = 1 }));
        var r = await Json(await c.PostAsync("/api/import?accountId=1", new StringContent("Date;Libellé;Montant\n30/10/2026;CB E.LECLERC LILLE;-85,40\n30/10/2026;BOULANGERIE;-3,20\n", Encoding.UTF8, "application/octet-stream")));
        Assert.Equal(1, r.GetProperty("reconciled").GetInt32());
        Assert.Equal(1, r.GetProperty("added").GetInt32());
        var all = await Json(await c.GetAsync("/api/transactions?limit=10"));
        Assert.Equal(2, all.GetProperty("total").GetInt32());
        Assert.Contains(all.GetProperty("items").EnumerateArray(), t => t.GetProperty("note").GetString() == "Leclerc" && t.GetProperty("remittance").GetString() == "CB E.LECLERC LILLE");
    }

    [Fact]
    public async Task Categories_can_be_created_with_unique_ids_and_names()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var a = await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Santé", group = "pot" }));
        Assert.Equal("sante", a.GetProperty("id").GetString());
        Assert.Equal("pot", a.GetProperty("group").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/categories", new { label = "  SANTE " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/categories", new { label = "  " })).StatusCode);
        var b = await Json(await c.PostAsJsonAsync("/api/categories", new { label = "Sante!" }));
        Assert.Equal("sante-2", b.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Remembered_bank_categories_can_be_listed_and_forgotten()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/paste/commit", new
        {
            accountId = 1,
            mappings = new[] { new { bankCategory = "Loyers et traites", categoryId = "divers", ignore = false } },
            items = new[] { new { date = "2026-11-01", label = "X", amount = -1m, providerId = "paste:x", action = "skip", @internal = false, pending = false } },
        }));
        var list = await Json(await c.GetAsync("/api/bank-categories"));
        Assert.Equal("divers", list[0].GetProperty("categoryId").GetString());
        var key = Uri.EscapeDataString(list[0].GetProperty("key").GetString()!);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/bank-categories?key=" + key)).StatusCode);
        Assert.Equal(0, (await Json(await c.GetAsync("/api/bank-categories"))).GetArrayLength());
    }

    [Fact]
    public async Task Bad_input_is_refused_and_the_endpoints_need_a_session()
    {
        using var f = Factory();
        var anon = f.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Preview(anon, "x")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/paste/commit", new { accountId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/categories", new { label = "x" })).StatusCode);

        var c = await f.LoggedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(c, "   ")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(c, "du texte sans opération")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Preview(c, PastedStatementParserTests.Sample, account: 99)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = Array.Empty<object>() })).StatusCode);

        var item = Items(await Json(await Preview(c, PastedStatementParserTests.Sample)))[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = new[] { Commit(item, categoryId: "inconnue") } })).StatusCode);
        // Rien n'a été écrit par la validation refusée.
        Assert.Equal(0, (await Json(await c.GetAsync("/api/transactions?limit=5"))).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Committing_twice_does_not_duplicate_and_a_forged_reconcile_is_skipped()
    {
        using var f = Factory();
        var c = await f.LoggedInAsync();
        var item = Items(await Json(await Preview(c, PastedStatementParserTests.Sample)))[0];
        var body = new { accountId = 1, items = new[] { Commit(item, categoryId: "shop") } };
        Assert.Equal(1, (await Json(await c.PostAsJsonAsync("/api/paste/commit", body))).GetProperty("added").GetInt32());
        var second = await Json(await c.PostAsJsonAsync("/api/paste/commit", body));
        Assert.Equal(0, second.GetProperty("added").GetInt32());
        Assert.Equal(1, second.GetProperty("skipped").GetInt32());

        var other = Items(await Json(await Preview(c, "AUTRE\nlun. 2 nov. à 08h00\nCarte bancaire\n-9,99 €")))[0];
        var forged = await Json(await c.PostAsJsonAsync("/api/paste/commit", new { accountId = 1, items = new[] { Commit(other, "reconcile", reconcileId: 1) } }));
        Assert.Equal(0, forged.GetProperty("reconciled").GetInt32());                       // le montant ne correspond pas : refusé, pas écrasé
        Assert.Equal(1, forged.GetProperty("skipped").GetInt32());
    }
}
