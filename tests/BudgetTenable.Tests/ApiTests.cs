using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BudgetTenable.Tests;

public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string Pin = "246810";
    public string DataDir { get; }
    // 30 octobre 2026, 12 h à Paris (heure d'hiver : UTC+1)
    public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 11, 0, 0, TimeSpan.Zero);
    private readonly bool _ownsDir;

    public AppFactory(string? dataDir = null)
    {
        _ownsDir = dataDir is null;
        DataDir = dataDir ?? Path.Combine(Path.GetTempPath(), "bt-tests-" + Guid.NewGuid().ToString("N"));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("BUDGET_DATA_DIR", DataDir);
        builder.UseSetting("BUDGET_PIN", Pin);
        builder.UseSetting("BUDGET_INSECURE_COOKIES", "true");
        builder.UseSetting("Auth:LoginPermitPerMinute", "1000");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(new FixedTime(Now)));
    }

    public async Task<HttpClient> LoggedInAsync()
    {
        var c = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var r = await c.PostAsJsonAsync("/api/auth/login", new { pin = Pin });
        r.EnsureSuccessStatusCode();
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDir)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(DataDir, true); } catch (IOException) { }
        }
    }
}

public class ApiTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static StringContent Body(string text) => new(text, Encoding.UTF8, "application/octet-stream");

    private static async Task<JsonElement> Import(HttpClient c, string csv, int account = 1) =>
        await Json(await c.PostAsync($"/api/import?accountId={account}", Body(csv)));

    // ---------- Authentification et en-têtes ----------

    [Fact]
    public async Task Api_requires_a_session_and_login_needs_the_right_pin()
    {
        using var f = new AppFactory();
        var anon = f.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/budget")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/export.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { pin = "000000" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/healthz")).StatusCode);

        var status = await Json(await anon.GetAsync("/api/auth/status"));
        Assert.True(status.GetProperty("required").GetBoolean());
        Assert.False(status.GetProperty("authenticated").GetBoolean());

        var ok = await anon.PostAsJsonAsync("/api/auth/login", new { pin = AppFactory.Pin });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var cookie = ok.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/budget")).StatusCode);
        await anon.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/budget")).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_expired_cookie_is_refused()
    {
        using var f = new AppFactory();
        var c = f.CreateClient();
        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/budget");
        forged.Headers.Add("Cookie", "bt_session=v1.99999999999.AAAA.deadbeef");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(forged)).StatusCode);

        // Un jeton valide émis aujourd'hui expire après la durée de session.
        var login = await f.CreateClient().PostAsJsonAsync("/api/auth/login", new { pin = AppFactory.Pin });
        var token = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        f.Now = f.Now.AddDays(31);
        var late = new HttpRequestMessage(HttpMethod.Get, "/api/budget");
        late.Headers.Add("Cookie", token);
        using var later = new AppFactory(f.DataDir) { Now = f.Now };
        Assert.Equal(HttpStatusCode.Unauthorized, (await later.CreateClient().SendAsync(late)).StatusCode);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_no_third_party_requests_are_allowed()
    {
        using var f = new AppFactory();
        var r = await f.CreateClient().GetAsync("/healthz");
        var csp = r.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", r.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-store", (await f.CreateClient().GetAsync("/api/auth/status")).Headers.CacheControl?.ToString());
    }

    // ---------- Budget et persistance ----------

    [Fact]
    public async Task Budget_starts_with_the_seed_and_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "bt-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var f = new AppFactory(dir))
            {
                var c = await f.LoggedInAsync();
                var b = await Json(await c.GetAsync("/api/budget"));
                Assert.Equal("2026-10", b.GetProperty("currentPeriod").GetString());
                Assert.Equal(28, b.GetProperty("settings").GetProperty("startDay").GetInt32());
                Assert.Equal(26, b.GetProperty("categories").GetArrayLength());

                var put = await c.PutAsJsonAsync("/api/budget", new
                {
                    settings = new { income = new { net = 2100, partner = 750, caf = 198 }, annualBonus = 3000, includeBonus = false, startDay = 1, potStart = "2026-12", potOpening = new { voiture = 200 }, lddsDone = new { } },
                    categories = new object[] { new { id = "shop", percentage = 12 }, new { id = "immo", amount = 950 } },
                });
                var saved = await Json(put);
                Assert.Equal(1, saved.GetProperty("settings").GetProperty("startDay").GetInt32());
            }

            using var again = new AppFactory(dir);
            var c2 = await again.LoggedInAsync();
            var b2 = await Json(await c2.GetAsync("/api/budget"));
            var s = b2.GetProperty("settings");
            Assert.Equal(2100m, s.GetProperty("income").GetProperty("net").GetDecimal());
            Assert.False(s.GetProperty("includeBonus").GetBoolean());
            Assert.Equal(200m, s.GetProperty("potOpening").GetProperty("voiture").GetDecimal());
            var cats = b2.GetProperty("categories").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!);
            Assert.Equal(12m, cats["shop"].GetProperty("percentage").GetDecimal());
            Assert.Equal(950m, cats["immo"].GetProperty("amount").GetDecimal());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(29)]
    public async Task Invalid_start_day_is_rejected(int day)
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        var r = await c.PutAsJsonAsync("/api/budget", new { settings = new { income = new { net = 1, partner = 1, caf = 1 }, startDay = day, potStart = "2026-11" } });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    // ---------- Saisie manuelle et suivi ----------

    [Fact]
    public async Task Manual_expenses_and_refunds_feed_the_period_summary()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();

        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-29", amount = -120.00, categoryId = "shop", note = "Manteau", accountId = 1 }));
        var refund = await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = 45.00, categoryId = "shop", note = "Retour" }));
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -62.10, categoryId = "carbu" }));
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -300, categoryId = "voiture" }));

        var s = await Json(await c.GetAsync("/api/periods/2026-10"));
        Assert.Equal(3, s.GetProperty("elapsed").GetInt32());
        Assert.Equal(137.10m, s.GetProperty("variableSpent").GetDecimal());   // 120 - 45 + 62,10 ; le pot n'y est pas
        var shop = s.GetProperty("envelopes").EnumerateArray().Single(e => e.GetProperty("categoryId").GetString() == "shop");
        Assert.Equal(75m, shop.GetProperty("spent").GetDecimal());
        var voiture = s.GetProperty("pots").EnumerateArray().Single(p => p.GetProperty("categoryId").GetString() == "voiture");
        Assert.Equal(300m, voiture.GetProperty("spentThisPeriod").GetDecimal());

        var list = await Json(await c.GetAsync("/api/transactions?period=2026-10&q=manteau"));
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.True(list.GetProperty("items")[0].GetProperty("isManual").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/transactions/{refund.GetProperty("id").GetInt32()}")).StatusCode);
        var after = await Json(await c.GetAsync("/api/periods/2026-10"));
        Assert.Equal(182.10m, after.GetProperty("variableSpent").GetDecimal());
    }

    [Fact]
    public async Task Internal_transfers_do_not_count_in_the_budget()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -326, isInternalTransfer = true, note = "Virement LDDS" }));
        var s = await Json(await c.GetAsync("/api/periods/2026-10"));
        Assert.Equal(0m, s.GetProperty("variableSpent").GetDecimal());
        Assert.False(s.GetProperty("hasTransactions").GetBoolean());
        var inbox = await Json(await c.GetAsync("/api/transactions/unsorted-count"));
        Assert.Equal(0, inbox.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Validation_errors_are_reported_in_french()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        var r = await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = 0, categoryId = "shop" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("montant", await r.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/transactions", new { amount = -5, categoryId = "nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync("/api/periods/not-a-period")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/unknown")).StatusCode);
    }

    // ---------- Import, doublons, règles ----------

    private const string Csv =
        "Date opération;Libellé;Montant\n" +
        "28/10/2026;CB E.LECLERC LILLE;-85,40\n" +
        "29/10/2026;PRLV FREE MOBILE;-12,99\n" +
        "29/10/2026;CAFE DU COIN;-2,50\n" +
        "29/10/2026;CAFE DU COIN;-2,50\n" +
        "30/10/2026;VIR CAF;198,00\n";

    [Fact]
    public async Task Importing_the_same_statement_ten_times_creates_no_duplicates()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();

        var first = await Import(c, Csv);
        Assert.Equal(5, first.GetProperty("added").GetInt32());
        Assert.Equal(0, first.GetProperty("duplicates").GetInt32());

        for (var i = 0; i < 10; i++)
        {
            var again = await Import(c, Csv);
            Assert.Equal(0, again.GetProperty("added").GetInt32());
            Assert.Equal(5, again.GetProperty("duplicates").GetInt32());
        }
        var all = await Json(await c.GetAsync("/api/transactions?limit=100"));
        Assert.Equal(5, all.GetProperty("total").GetInt32());   // les deux cafés identiques du même jour sont bien deux opérations
    }

    [Fact]
    public async Task Overlapping_statements_only_add_the_new_lines()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        await Import(c, Csv);
        var next = Csv + "31/10/2026;BOULANGERIE;-3,20\n";
        var r = await Import(c, next);
        Assert.Equal(1, r.GetProperty("added").GetInt32());
        Assert.Equal(5, r.GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task Same_operations_on_two_accounts_are_distinct()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        await Import(c, Csv, 1);
        var r = await Import(c, Csv, 2);
        Assert.Equal(5, r.GetProperty("added").GetInt32());
    }

    [Fact]
    public async Task A_rule_classifies_the_inbox_and_new_imports_but_never_overrides_a_manual_choice()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        var imp = await Import(c, Csv);
        Assert.Equal(5, imp.GetProperty("toSort").GetInt32());

        // Tri manuel d'une opération, puis création de la règle proposée.
        var list = await Json(await c.GetAsync("/api/transactions?unsorted=true"));
        var leclerc = list.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("remittance").GetString()!.Contains("LECLERC"));
        Assert.Equal("E.LECLERC LILLE", leclerc.GetProperty("suggestedPattern").GetString());
        await Json(await c.PutAsJsonAsync($"/api/transactions/{leclerc.GetProperty("id").GetInt32()}", new { categoryId = "resto", note = "à part" }));

        var created = await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "leclerc", categoryId = "courses", priority = 1 }));
        Assert.Equal(0, created.GetProperty("applied").GetInt32());   // la seule opération Leclerc a déjà été triée à la main

        await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "free mobile", categoryId = "tel" }));
        await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "vir caf", categoryId = "rev-caf" }));
        var inbox = await Json(await c.GetAsync("/api/transactions/unsorted-count"));
        Assert.Equal(2, inbox.GetProperty("count").GetInt32());   // les deux cafés restent à trier

        // Tout ré-évaluer reclasse les 2 lignes déjà posées par règle ; le classement manuel de Leclerc ne bouge pas.
        var rules = await Json(await c.PostAsJsonAsync("/api/rules/apply", new { all = true }));
        Assert.Equal(2, rules.GetProperty("applied").GetInt32());
        var leclercAfter = await Json(await c.GetAsync("/api/transactions?q=leclerc"));
        Assert.Equal("resto", leclercAfter.GetProperty("items")[0].GetProperty("categoryId").GetString());
        Assert.Equal("manual", leclercAfter.GetProperty("items")[0].GetProperty("source").GetString());

        // Un nouvel import est classé à l'arrivée.
        var more = await Import(c, "Date;Libellé;Montant\n02/11/2026;PRLV FREE MOBILE;-12,99\n");
        Assert.Equal(1, more.GetProperty("categorized").GetInt32());
    }

    [Fact]
    public async Task Internal_transfer_rules_remove_operations_from_the_inbox()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        await Import(c, "Date;Libellé;Montant\n03/11/2026;VIREMENT VERS LDDS;-326,00\n");
        var created = await Json(await c.PostAsJsonAsync("/api/rules", new { pattern = "vers ldds", marksInternalTransfer = true }));
        Assert.Equal(1, created.GetProperty("applied").GetInt32());
        var count = await Json(await c.GetAsync("/api/transactions/unsorted-count"));
        Assert.Equal(0, count.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task Invalid_rules_and_unreadable_files_are_refused()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/rules", new { pattern = "([", isRegex = true, categoryId = "shop" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/rules", new { pattern = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/rules", new { pattern = "x", categoryId = "inconnue" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/import?accountId=1", Body("a;b\n1;2\n"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/import?accountId=99", Body(Csv))).StatusCode);
    }

    [Fact]
    public async Task Ofx_import_uses_bank_identifiers()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        const string ofx = "OFXHEADER:100\n<OFX><STMTTRN><DTPOSTED>20261028<TRNAMT>-10.00<FITID>Z1<NAME>BOULANGERIE\n<STMTTRN><DTPOSTED>20261029<TRNAMT>-20.00<FITID>Z2<NAME>MARCHE\n</OFX>";
        var r = await Import(c, ofx);
        Assert.Equal("ofx", r.GetProperty("format").GetString());
        Assert.Equal(2, r.GetProperty("added").GetInt32());
        Assert.Equal(2, (await Import(c, ofx)).GetProperty("duplicates").GetInt32());
    }

    [Fact]
    public async Task CSV_export_neutralizes_spreadsheet_formulas()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-30", amount = -5, categoryId = "shop", note = "=HYPERLINK(\"http://x\")" }));
        var csv = await (await c.GetAsync("/api/export.csv")).Content.ReadAsStringAsync();
        Assert.StartsWith("﻿Date;Catégorie", csv);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv);
        Assert.Contains(";-5,00;non", csv);
    }

    [Fact]
    public async Task Rate_limit_blocks_pin_guessing()
    {
        using var f = new AppFactory();
        var limited = f.WithWebHostBuilder(b => b.UseSetting("Auth:LoginPermitPerMinute", "3"));
        var c = limited.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++) codes.Add((await c.PostAsJsonAsync("/api/auth/login", new { pin = "000000" })).StatusCode);
        Assert.Contains(HttpStatusCode.TooManyRequests, codes);
    }
}
