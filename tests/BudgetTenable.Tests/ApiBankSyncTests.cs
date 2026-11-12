using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BudgetTenable.Api.Banking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace BudgetTenable.Tests;

public sealed class MutableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed record RecordedRequest(string Method, string Path, string Query, Dictionary<string, string> Headers, string? Body);

/// <summary>Fausse API Enable Banking : mêmes chemins et mêmes formes JSON que la documentation officielle.</summary>
public sealed class FakeEnableBanking(RSA publicKey, string appId) : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();
    public string BankName { get; set; } = "Banque Test";
    public long MaxConsentSeconds { get; set; } = 180L * 86400;
    public List<string> RequiredPsuHeaders { get; set; } = new();
    public string SessionId { get; set; } = "11111111-1111-4111-8111-111111111111";
    public List<object> Accounts { get; set; } = new()
    {
        new { account_id = new { iban = "FR7630006000011234567890189" }, name = "M GUILLAUME", details = "Compte courant", cash_account_type = "CACC", currency = "EUR", uid = "uid-perso", identification_hash = "hash-perso" },
        new { account_id = new { iban = "FR7630006000019876543210123" }, name = "M ET MME", details = "Compte joint", cash_account_type = "CACC", currency = "EUR", uid = "uid-commun", identification_hash = "hash-commun" },
    };
    /// <summary>Pages d'opérations par compte ; la clé de continuation d'une page est « page-N ».</summary>
    public Dictionary<string, List<object[]>> Pages { get; } = new();
    /// <summary>Erreur à renvoyer aux lectures d'opérations : (statut HTTP, code d'erreur).</summary>
    public (int Status, string Error)? TransactionsError { get; set; }
    public (int Status, string Error)? SessionError { get; set; }
    public string? LastState { get; private set; }
    public JsonElement LastAuthBody { get; private set; }
    public bool AllTokensValid { get; private set; } = true;

    public void Feed(string uid, params object[] transactions) => Pages[uid] = new List<object[]> { transactions };
    public int Count(string method, string pathPrefix) => Requests.Count(r => r.Method == method && r.Path.StartsWith(pathPrefix, StringComparison.Ordinal));

    public static object Tx(string? reference, string date, decimal amount, string party, string status = "BOOK", string? remittance = null) => new
    {
        entry_reference = reference,
        transaction_amount = new { currency = "EUR", amount = Math.Abs(amount).ToString("0.00", CultureInfo.InvariantCulture) },
        creditor = amount < 0 ? new { name = party } : null,
        debtor = amount < 0 ? null : new { name = party },
        credit_debit_indicator = amount < 0 ? "DBIT" : "CRDT",
        status,
        booking_date = status == "BOOK" ? date : null,
        transaction_date = date,
        remittance_information = remittance is null ? null : new[] { remittance },
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        Requests.Add(new RecordedRequest(request.Method.Method, path, query,
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));
        if (!TokenIsValid(request.Headers.Authorization?.Parameter)) { AllTokensValid = false; return Error(401, "UNAUTHORIZED_ACCESS"); }

        if (request.Method == HttpMethod.Get && path == "/aspsps")
            return Ok(new { aspsps = new object[] {
                new { name = BankName, country = "FR", psu_types = new[] { "personal" }, maximum_consent_validity = MaxConsentSeconds, beta = false, required_psu_headers = RequiredPsuHeaders },
                new { name = "Autre Banque", country = "FR", psu_types = new[] { "personal" }, maximum_consent_validity = 90L * 86400, beta = true, required_psu_headers = Array.Empty<string>() },
            } });

        if (request.Method == HttpMethod.Post && path == "/auth")
        {
            LastAuthBody = JsonDocument.Parse(body!).RootElement.Clone();
            LastState = LastAuthBody.GetProperty("state").GetString();
            return Ok(new { url = "https://auth.enablebanking.com/ais/start?sessionid=73100c65", authorization_id = "73100c65-c54d-46a1-87d1-aa3effde435a" });
        }

        if (request.Method == HttpMethod.Post && path == "/sessions")
        {
            if (SessionError is { } se) return Error(se.Status, se.Error);
            return Ok(new { session_id = SessionId, accounts = Accounts, aspsp = new { name = BankName, country = "FR" }, psu_type = "personal",
                access = new { valid_until = LastAuthBody.GetProperty("access").GetProperty("valid_until").GetString() } });
        }

        if (request.Method == HttpMethod.Delete && path.StartsWith("/sessions/", StringComparison.Ordinal)) return Ok(new { message = "OK" });

        if (request.Method == HttpMethod.Get && path.StartsWith("/accounts/", StringComparison.Ordinal) && path.EndsWith("/transactions", StringComparison.Ordinal))
        {
            if (TransactionsError is { } te) return Error(te.Status, te.Error);
            var uid = path.Split('/')[2];
            var pages = Pages.GetValueOrDefault(uid) ?? new List<object[]> { Array.Empty<object>() };
            var key = System.Web.HttpUtility.ParseQueryString(query)["continuation_key"];
            var index = key is null ? 0 : int.Parse(key["page-".Length..], CultureInfo.InvariantCulture);
            return Ok(new { transactions = pages[index], continuation_key = index + 1 < pages.Count ? "page-" + (index + 1) : null });
        }
        return Error(404, "NOT_FOUND");
    }

    private bool TokenIsValid(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: 3 }) return false;
        try
        {
            if (!publicKey.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Unbase64(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
            using var header = JsonDocument.Parse(Unbase64(parts[0]));
            using var payload = JsonDocument.Parse(Unbase64(parts[1]));
            var p = payload.RootElement;
            return header.RootElement.GetProperty("alg").GetString() == "RS256" && header.RootElement.GetProperty("typ").GetString() == "JWT"
                && header.RootElement.GetProperty("kid").GetString() == appId
                && p.GetProperty("iss").GetString() == "enablebanking.com" && p.GetProperty("aud").GetString() == "api.enablebanking.com"
                && p.GetProperty("exp").GetInt64() - p.GetProperty("iat").GetInt64() is > 0 and <= 86400;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException) { return false; }
    }

    private static byte[] Unbase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static HttpResponseMessage Ok(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    private static HttpResponseMessage Error(int status, string error) =>
        new((HttpStatusCode)status) { Content = JsonContent.Create(new { message = "détail interne à ne pas afficher", code = status, error }) };
}

/// <summary>Application de test avec la synchro bancaire configurée et la fausse API branchée.</summary>
public sealed class BankApp : IDisposable
{
    public const string AppId = "cf589be3-3755-465b-a8df-a90a16a31403";
    private readonly AppFactory _root = new();
    private readonly RSA _rsa = RSA.Create(2048);
    public WebApplicationFactory<Program> App { get; }
    public FakeEnableBanking Bank { get; }
    // 30 octobre 2026, 12 h à Paris : période « 2026-10 » (28 octobre au 27 novembre).
    public MutableTime Time { get; } = new(new DateTimeOffset(2026, 10, 30, 11, 0, 0, TimeSpan.Zero));

    public BankApp()
    {
        Bank = new FakeEnableBanking(_rsa, AppId);
        App = _root.WithWebHostBuilder(b =>
        {
            b.UseSetting("BUDGET_EB_APP_ID", AppId);
            b.UseSetting("BUDGET_EB_KEY", _rsa.ExportRSAPrivateKeyPem());
            b.UseSetting("BUDGET_EB_REDIRECT_URL", "https://budget.example.fr/api/bank/callback");
            b.ConfigureTestServices(s =>
            {
                s.AddSingleton<TimeProvider>(Time);
                s.AddHttpClient<IEnableBankingClient, EnableBankingClient>().ConfigurePrimaryHttpMessageHandler(() => Bank);
            });
        });
    }

    public HttpClient Anonymous() => App.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    public async Task<HttpClient> LoggedInAsync()
    {
        var c = Anonymous();
        (await c.PostAsJsonAsync("/api/auth/login", new { pin = AppFactory.Pin })).EnsureSuccessStatusCode();
        c.DefaultRequestHeaders.UserAgent.ParseAdd("BudgetTests/1.0");
        return c;
    }

    /// <summary>Parcours complet : choix de la banque, retour de la banque, puis rattachement du premier compte au compte « Perso ».</summary>
    public async Task<HttpClient> ConnectedAsync(bool link = true)
    {
        var c = await LoggedInAsync();
        (await c.PostAsJsonAsync("/api/bank/connect", new { name = Bank.BankName })).EnsureSuccessStatusCode();
        var back = await c.GetAsync($"/api/bank/callback?code=abc&state={Bank.LastState}");
        Assert.Equal("/?banque=ok#regles", back.Headers.Location?.OriginalString);
        if (link)
        {
            var status = await ApiBankSyncTests.Json(await c.GetAsync("/api/bank/status"));
            var first = status.GetProperty("connections")[0].GetProperty("accounts")[0].GetProperty("id").GetInt32();
            (await c.PutAsJsonAsync($"/api/bank/accounts/{first}", new { accountId = 1 })).EnsureSuccessStatusCode();
        }
        return c;
    }

    public void Dispose()
    {
        App.Dispose();
        _root.Dispose();
        _rsa.Dispose();
    }
}

public class ApiBankSyncTests
{
    internal static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<JsonElement[]> Transactions(HttpClient c) =>
        (await Json(await c.GetAsync("/api/transactions?limit=2000"))).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<JsonElement> Sync(HttpClient c) => (await Json(await c.PostAsync("/api/bank/sync", null))).GetProperty("results")[0];

    private static async Task<JsonElement> Connection(HttpClient c) => (await Json(await c.GetAsync("/api/bank/status"))).GetProperty("connections")[0];

    private static object Tx(string? reference, string date, decimal amount, string party, string status = "BOOK", string? remittance = null) =>
        FakeEnableBanking.Tx(reference, date, amount, party, status, remittance);

    // ---------- Configuration et accès ----------

    [Fact]
    public async Task Without_configuration_the_bank_sync_is_off_and_says_so()
    {
        using var f = new AppFactory();
        var c = await f.LoggedInAsync();
        var status = await Json(await c.GetAsync("/api/bank/status"));
        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(0, status.GetProperty("connections").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/bank/sync", null)).StatusCode);
    }

    [Fact]
    public void An_application_id_without_its_private_key_stops_the_startup_with_a_clear_message()
    {
        using var root = new AppFactory();
        using var app = root.WithWebHostBuilder(b => b.UseSetting("BUDGET_EB_APP_ID", BankApp.AppId));
        var e = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("BUDGET_EB_KEY", e.ToString());
    }

    [Fact]
    public async Task Only_the_bank_callback_is_public_and_it_does_nothing_without_a_known_state()
    {
        using var app = new BankApp();
        var anon = app.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/bank/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/bank/sync", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/bank/aspsps")).StatusCode);

        var back = await anon.GetAsync("/api/bank/callback?code=abc&state=jeton-invente");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/?banque=invalide#regles", back.Headers.Location?.OriginalString);
        Assert.Empty(app.Bank.Requests);   // aucun appel à l'agrégateur pour un jeton inconnu
    }

    // ---------- Connexion ----------

    [Fact]
    public async Task Connecting_asks_the_bank_for_its_maximum_consent_and_signs_requests_with_the_application_key()
    {
        using var app = new BankApp();
        var c = await app.LoggedInAsync();

        var banks = await Json(await c.GetAsync("/api/bank/aspsps"));
        Assert.Equal(new[] { "Autre Banque", "Banque Test" }, banks.EnumerateArray().Select(b => b.GetProperty("name").GetString()).ToArray());
        Assert.Equal(180, banks[1].GetProperty("maxConsentDays").GetInt32());

        var r = await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        Assert.StartsWith("https://auth.enablebanking.com/", r.GetProperty("url").GetString());

        var auth = app.Bank.LastAuthBody;
        Assert.Equal("Banque Test", auth.GetProperty("aspsp").GetProperty("name").GetString());
        Assert.Equal("FR", auth.GetProperty("aspsp").GetProperty("country").GetString());
        Assert.Equal("https://budget.example.fr/api/bank/callback", auth.GetProperty("redirect_url").GetString());
        Assert.Equal("personal", auth.GetProperty("psu_type").GetString());
        Assert.Equal(64, auth.GetProperty("state").GetString()!.Length);
        // Fin du consentement : le maximum annoncé par la banque (180 jours), moins une marge de cinq minutes, avec fuseau explicite.
        var validUntil = DateTimeOffset.Parse(auth.GetProperty("access").GetProperty("valid_until").GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(app.Time.Now.AddDays(180).AddMinutes(-5), validUntil);
        Assert.EndsWith("+00:00", auth.GetProperty("access").GetProperty("valid_until").GetString());
        Assert.True(app.Bank.AllTokensValid);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Inconnue" })).StatusCode);
    }

    [Fact]
    public async Task A_bank_with_a_shorter_consent_gets_a_shorter_one()
    {
        using var app = new BankApp();
        app.Bank.MaxConsentSeconds = 90L * 86400;
        var c = await app.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        var validUntil = DateTimeOffset.Parse(app.Bank.LastAuthBody.GetProperty("access").GetProperty("valid_until").GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(app.Time.Now.AddDays(90).AddMinutes(-5), validUntil);
    }

    [Fact]
    public async Task The_callback_creates_the_connection_once_and_never_exposes_the_full_iban()
    {
        using var app = new BankApp();
        var c = await app.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        var state = app.Bank.LastState;

        // Le retour arrive parfois dans un navigateur sans session (application installée) : il doit marcher quand même.
        var back = await app.Anonymous().GetAsync($"/api/bank/callback?code=abc&state={state}");
        Assert.Equal("/?banque=ok#regles", back.Headers.Location?.OriginalString);

        var raw = await (await c.GetAsync("/api/bank/status")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("3000600001", raw);            // aucun IBAN complet
        Assert.DoesNotContain("hash-perso", raw);            // ni l'empreinte du compte
        Assert.DoesNotContain(app.Bank.SessionId, raw);      // ni l'identifiant de session
        var conn = await Connection(c);
        Assert.Equal("Banque Test", conn.GetProperty("bank").GetString());
        Assert.Equal("active", conn.GetProperty("status").GetString());
        Assert.Equal(179, conn.GetProperty("daysLeft").GetInt32());
        Assert.False(conn.GetProperty("expiresSoon").GetBoolean());
        var accounts = conn.GetProperty("accounts").EnumerateArray().ToArray();
        Assert.Equal(new[] { "Compte courant", "Compte joint" }, accounts.Select(a => a.GetProperty("label").GetString()).ToArray());
        Assert.Equal("FR•• •••• 0189", accounts[0].GetProperty("ibanMasked").GetString());
        Assert.All(accounts, a => Assert.Equal(JsonValueKind.Null, a.GetProperty("accountId").ValueKind));   // rien n'est synchronisé sans choix

        // Le jeton ne sert qu'une fois.
        var replay = await c.GetAsync($"/api/bank/callback?code=abc&state={state}");
        Assert.Equal("/?banque=invalide#regles", replay.Headers.Location?.OriginalString);
        Assert.Equal(1, (await Json(await c.GetAsync("/api/bank/status"))).GetProperty("connections").GetArrayLength());
    }

    [Theory]
    [InlineData("error=access_denied&error_description=Cancelled+by+user", "refuse")]
    [InlineData("error=server_error", "erreur")]
    [InlineData("", "erreur")]
    public async Task A_refusal_or_an_error_at_the_bank_creates_nothing(string query, string outcome)
    {
        using var app = new BankApp();
        var c = await app.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        var back = await c.GetAsync($"/api/bank/callback?state={app.Bank.LastState}&{query}");
        Assert.Equal($"/?banque={outcome}#regles", back.Headers.Location?.OriginalString);
        Assert.Equal(0, (await Json(await c.GetAsync("/api/bank/status"))).GetProperty("connections").GetArrayLength());
        Assert.Equal(0, app.Bank.Count("POST", "/sessions"));
    }

    [Fact]
    public async Task A_state_older_than_one_hour_is_refused()
    {
        using var app = new BankApp();
        var c = await app.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        app.Time.Now = app.Time.Now.AddMinutes(61);
        var back = await c.GetAsync($"/api/bank/callback?code=abc&state={app.Bank.LastState}");
        Assert.Equal("/?banque=expire#regles", back.Headers.Location?.OriginalString);
        Assert.Equal(0, app.Bank.Count("POST", "/sessions"));
    }

    [Fact]
    public async Task Accounts_not_linked_in_the_enable_banking_portal_are_reported()
    {
        using var app = new BankApp();
        app.Bank.SessionError = (422, "NO_ACCOUNTS_ADDED");
        var c = await app.LoggedInAsync();
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test" }));
        var back = await c.GetAsync($"/api/bank/callback?code=abc&state={app.Bank.LastState}");
        Assert.Equal("/?banque=aucun-compte#regles", back.Headers.Location?.OriginalString);
    }

    // ---------- Rattachement des comptes ----------

    [Fact]
    public async Task A_bank_account_feeds_one_account_of_the_app_or_a_new_one_or_none()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        var accounts = (await Connection(c)).GetProperty("accounts").EnumerateArray().ToArray();
        int perso = accounts[0].GetProperty("id").GetInt32(), joint = accounts[1].GetProperty("id").GetInt32();
        Assert.Equal(1, accounts[0].GetProperty("accountId").GetInt32());

        // Deux comptes bancaires ne peuvent pas alimenter le même compte : leurs références d'opérations se mélangeraient.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/bank/accounts/{joint}", new { accountId = 1 })).StatusCode);
        Assert.Equal(2, (await Json(await c.PutAsJsonAsync($"/api/bank/accounts/{joint}", new { accountId = 2 }))).GetProperty("accountId").GetInt32());

        // « Ne pas synchroniser », puis création d'un compte dédié.
        Assert.Equal(JsonValueKind.Null, (await Json(await c.PutAsJsonAsync($"/api/bank/accounts/{joint}", new { }))).GetProperty("accountId").ValueKind);
        var created = (await Json(await c.PutAsJsonAsync($"/api/bank/accounts/{joint}", new { create = true }))).GetProperty("accountId").GetInt32();
        var all = await Json(await c.GetAsync("/api/accounts"));
        Assert.Contains(all.EnumerateArray(), a => a.GetProperty("id").GetInt32() == created && a.GetProperty("label").GetString() == "Compte joint");

        Assert.Equal(HttpStatusCode.NotFound, (await c.PutAsJsonAsync("/api/bank/accounts/999", new { accountId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/bank/accounts/{perso}", new { accountId = 999 })).StatusCode);
    }

    // ---------- Synchronisation ----------

    [Fact]
    public async Task The_first_sync_reads_ninety_days_and_applies_signs_statuses_and_rules()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        (await c.PostAsJsonAsync("/api/rules", new { pattern = "LECLERC", categoryId = "courses" })).EnsureSuccessStatusCode();
        app.Bank.Feed("uid-perso",
            Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"),
            Tx("R2", "2026-10-29", 198.00m, "CAF DU NORD"),
            Tx(null, "2026-10-30", -12.90m, "BOULANGERIE PAUL", "PDNG"),
            Tx("R3", "2026-10-28", -30.00m, "ANNULEE", "CNCL"),
            Tx("R4", "2026-11-05", -92.00m, "FREE", "SCHD"));

        var r = await Sync(c);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("error").ValueKind);
        Assert.Equal(3, r.GetProperty("added").GetInt32());

        var tx = await Transactions(c);
        Assert.Equal(3, tx.Length);
        var leclerc = tx.Single(t => t.GetProperty("counterparty").GetString() == "E.LECLERC LILLE");
        Assert.Equal(-54.20m, leclerc.GetProperty("amount").GetDecimal());
        Assert.Equal("courses", leclerc.GetProperty("categoryId").GetString());
        Assert.Equal("booked", leclerc.GetProperty("status").GetString());
        Assert.False(leclerc.GetProperty("isManual").GetBoolean());
        Assert.Equal(198.00m, tx.Single(t => t.GetProperty("counterparty").GetString() == "CAF DU NORD").GetProperty("amount").GetDecimal());
        Assert.Equal("pending", tx.Single(t => t.GetProperty("counterparty").GetString() == "BOULANGERIE PAUL").GetProperty("status").GetString());

        // Première lecture : 90 jours en arrière, stratégie « longest », seulement le compte relié.
        var read = Assert.Single(app.Bank.Requests, x => x.Path.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Equal("/accounts/uid-perso/transactions", read.Path);
        Assert.Contains("date_from=2026-08-01", read.Query);
        Assert.Contains("strategy=longest", read.Query);

        var conn = await Connection(c);
        Assert.Equal("2026-10-30", conn.GetProperty("accounts")[0].GetProperty("syncedThrough").GetString());
        Assert.Equal(JsonValueKind.Null, conn.GetProperty("lastError").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, conn.GetProperty("lastSuccessAt").ValueKind);
    }

    [Fact]
    public async Task Ten_consecutive_syncs_never_duplicate_an_operation()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Feed("uid-perso",
            Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"),
            Tx("R2", "2026-10-29", -1.20m, "CAFE"),
            Tx(null, "2026-10-29", -1.20m, "DISTRIBUTEUR"),      // sans référence : deux fois la même le même jour
            Tx(null, "2026-10-29", -1.20m, "DISTRIBUTEUR"),
            Tx(null, "2026-10-30", -12.90m, "BOULANGERIE PAUL", "PDNG"));

        Assert.Equal(5, (await Sync(c)).GetProperty("added").GetInt32());
        for (var i = 0; i < 9; i++)
        {
            var r = await Sync(c);
            Assert.Equal(0, r.GetProperty("added").GetInt32());
            Assert.Equal(0, r.GetProperty("removedPending").GetInt32());
        }
        Assert.Equal(5, (await Transactions(c)).Length);

        // Les relectures repartent de dix jours avant la dernière synchro, en stratégie « default ».
        var last = app.Bank.Requests.Last(x => x.Path.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Contains("date_from=2026-10-20", last.Query);
        Assert.Contains("strategy=default", last.Query);
    }

    [Fact]
    public async Task All_pages_are_read_even_after_an_empty_one_and_overlapping_pages_do_not_duplicate()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Pages["uid-perso"] = new List<object[]>
        {
            new[] { Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"), Tx("R2", "2026-10-29", -9.90m, "PHARMACIE") },
            Array.Empty<object>(),                                    // page vide avec clé de continuation : ce n'est pas la fin
            new[] { Tx("R2", "2026-10-29", -9.90m, "PHARMACIE"), Tx("R3", "2026-10-28", -23.00m, "FREE MOBILE") },
        };

        Assert.Equal(3, (await Sync(c)).GetProperty("added").GetInt32());
        Assert.Equal(3, (await Transactions(c)).Length);

        var reads = app.Bank.Requests.Where(x => x.Path.EndsWith("/transactions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, reads.Length);
        Assert.DoesNotContain("continuation_key", reads[0].Query);
        Assert.Contains("continuation_key=page-1", reads[1].Query);
        Assert.Contains("continuation_key=page-2", reads[2].Query);
        // Les autres paramètres restent identiques d'une page à l'autre, comme l'exige l'API.
        Assert.All(reads, x => Assert.StartsWith("?date_from=2026-08-01&strategy=longest", x.Query));
    }

    [Fact]
    public async Task A_pending_operation_becomes_booked_and_keeps_its_category()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Feed("uid-perso", Tx(null, "2026-10-29", -12.90m, "BOULANGERIE PAUL", "PDNG"));
        await Sync(c);
        var pending = Assert.Single(await Transactions(c));
        Assert.Equal("pending", pending.GetProperty("status").GetString());
        (await c.PutAsJsonAsync($"/api/transactions/{pending.GetProperty("id").GetInt32()}", new { categoryId = "courses" })).EnsureSuccessStatusCode();

        // Deux jours plus tard, la banque la comptabilise : nouvelle date, référence définitive.
        app.Time.Now = app.Time.Now.AddDays(2);
        app.Bank.Feed("uid-perso", Tx("R9", "2026-10-31", -12.90m, "BOULANGERIE PAUL LILLE"));
        var r = await Sync(c);
        Assert.Equal(0, r.GetProperty("added").GetInt32());
        Assert.Equal(1, r.GetProperty("reconciled").GetInt32());

        var booked = Assert.Single(await Transactions(c));
        Assert.Equal("booked", booked.GetProperty("status").GetString());
        Assert.Equal("2026-10-31", booked.GetProperty("date").GetString());
        Assert.Equal("courses", booked.GetProperty("categoryId").GetString());
        Assert.Equal(pending.GetProperty("id").GetInt32(), booked.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task A_pending_operation_that_vanished_or_was_booked_for_another_amount_is_not_counted_twice()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        // Pré-autorisation de 150 € à la pompe, et une réservation d'hôtel finalement annulée.
        app.Bank.Feed("uid-perso",
            Tx(null, "2026-10-29", -150.00m, "TOTAL STATION", "PDNG"),
            Tx(null, "2026-10-29", -80.00m, "HOTEL IBIS", "HOLD"),
            Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));
        await Sync(c);
        Assert.Equal(3, (await Transactions(c)).Length);

        app.Time.Now = app.Time.Now.AddDays(2);
        app.Bank.Feed("uid-perso",
            Tx("R2", "2026-10-31", -62.40m, "TOTAL STATION"),
            Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));
        var r = await Sync(c);
        Assert.Equal(1, r.GetProperty("added").GetInt32());
        Assert.Equal(2, r.GetProperty("removedPending").GetInt32());

        var tx = await Transactions(c);
        Assert.Equal(new[] { -62.40m, -54.20m }, tx.Select(t => t.GetProperty("amount").GetDecimal()).OrderBy(a => a).ToArray());
        Assert.All(tx, t => Assert.Equal("booked", t.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task A_manual_entry_is_reconciled_with_the_bank_line_instead_of_being_duplicated()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        var manual = await Json(await c.PostAsJsonAsync("/api/transactions", new { date = "2026-10-28", amount = -23.50m, categoryId = "resto", note = "Pizza" }));

        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-30", -23.50m, "PIZZERIA NAPOLI"));
        var r = await Sync(c);
        Assert.Equal(0, r.GetProperty("added").GetInt32());
        Assert.Equal(1, r.GetProperty("reconciled").GetInt32());

        var tx = Assert.Single(await Transactions(c));
        Assert.Equal(manual.GetProperty("id").GetInt32(), tx.GetProperty("id").GetInt32());
        Assert.Equal("resto", tx.GetProperty("categoryId").GetString());
        Assert.Equal("Pizza", tx.GetProperty("note").GetString());
        Assert.False(tx.GetProperty("isManual").GetBoolean());

        Assert.Equal(0, (await Sync(c)).GetProperty("added").GetInt32());
        Assert.Single(await Transactions(c));
    }

    [Fact]
    public async Task Operations_already_imported_from_a_file_are_adopted_not_duplicated()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        // Le relevé CSV porte la date d'achat ; l'API de la banque donne la date comptable, le lendemain.
        var csv = "Date;Libellé;Montant\n27/10/2026;CB LECLERC LILLE;-54,20\n27/10/2026;CB DECATHLON;-54,20\n";
        var imported = await Json(await c.PostAsync("/api/import?accountId=1", new StringContent(csv, Encoding.UTF8, "application/octet-stream")));
        Assert.Equal(2, imported.GetProperty("added").GetInt32());

        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-28", -54.20m, "E.LECLERC LILLE"), Tx("R2", "2026-10-28", -54.20m, "DECATHLON VILLENEUVE"));
        var r = await Sync(c);
        Assert.Equal(0, r.GetProperty("added").GetInt32());
        Assert.Equal(2, r.GetProperty("reconciled").GetInt32());
        Assert.Equal(2, (await Transactions(c)).Length);

        var again = await Sync(c);
        Assert.Equal(0, again.GetProperty("added").GetInt32());
        Assert.Equal(2, (await Transactions(c)).Length);
    }

    [Fact]
    public async Task Only_linked_accounts_are_read()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync(link: false);
        var r = await Sync(c);
        Assert.Equal(0, r.GetProperty("added").GetInt32());
        Assert.Equal(0, app.Bank.Count("GET", "/accounts/"));
    }

    // ---------- Erreurs et expiration ----------

    [Fact]
    public async Task An_expired_session_asks_for_a_reconnection_and_stops_calling_the_bank()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.TransactionsError = (401, "EXPIRED_SESSION");

        var r = await Sync(c);
        Assert.Contains("reconnecte", r.GetProperty("error").GetString());
        Assert.DoesNotContain("détail interne", r.GetProperty("error").GetString());   // le message de l'agrégateur n'est pas recopié
        var conn = await Connection(c);
        Assert.Equal("expired", conn.GetProperty("status").GetString());
        Assert.Contains("reconnecte", conn.GetProperty("lastError").GetString());

        var before = app.Bank.Requests.Count;
        Assert.Equal(0, (await Json(await c.PostAsync("/api/bank/sync", null))).GetProperty("results").GetArrayLength());
        Assert.Equal(before, app.Bank.Requests.Count);
    }

    [Fact]
    public async Task The_bank_rate_limit_keeps_the_connection_active_and_explains_itself()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.TransactionsError = (429, "ASPSP_RATE_LIMIT_EXCEEDED");
        Assert.Contains("limite", (await Sync(c)).GetProperty("error").GetString());
        Assert.Equal("active", (await Connection(c)).GetProperty("status").GetString());

        // À la synchro suivante, tout rentre dans l'ordre.
        app.Bank.TransactionsError = null;
        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));
        var r = await Sync(c);
        Assert.Equal(1, r.GetProperty("added").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await Connection(c)).GetProperty("lastError").ValueKind);
    }

    [Fact]
    public async Task The_consent_end_is_announced_fourteen_days_ahead_then_marked_expired()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();

        // La session de l'appli (30 jours) expire bien avant le consentement bancaire : on se reconnecte à chaque saut.
        app.Time.Now = app.Time.Now.AddDays(170);
        c = await app.LoggedInAsync();
        var soon = await Connection(c);
        Assert.Equal("active", soon.GetProperty("status").GetString());
        Assert.True(soon.GetProperty("expiresSoon").GetBoolean());
        Assert.Equal(9, soon.GetProperty("daysLeft").GetInt32());

        app.Time.Now = app.Time.Now.AddDays(11);
        c = await app.LoggedInAsync();
        var gone = await Connection(c);
        Assert.Equal("expired", gone.GetProperty("status").GetString());
        Assert.Equal(0, (await Json(await c.PostAsync("/api/bank/sync", null))).GetProperty("results").GetArrayLength());
        Assert.Equal(0, app.Bank.Count("GET", "/accounts/"));
    }

    // ---------- Synchro planifiée et en-têtes de présence ----------

    [Fact]
    public async Task The_scheduled_sync_waits_six_hours_between_two_readings_and_never_claims_the_user_is_present()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));

        async Task<int> Scheduled()
        {
            await using var scope = app.App.Services.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<BankSyncService>().SyncAsync(null, onlyDue: true, CancellationToken.None)).Results.Count;
        }

        Assert.Equal(1, await Scheduled());
        Assert.Equal(0, await Scheduled());                       // trop tôt : la banque n'est pas rappelée
        app.Time.Now = app.Time.Now.AddHours(5).AddMinutes(59);
        Assert.Equal(0, await Scheduled());
        app.Time.Now = app.Time.Now.AddMinutes(1);
        Assert.Equal(1, await Scheduled());

        var reads = app.Bank.Requests.Where(x => x.Path.EndsWith("/transactions", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, x => Assert.DoesNotContain(x.Headers.Keys, k => k.StartsWith("Psu-", StringComparison.OrdinalIgnoreCase)));
        Assert.Single(await Transactions(c));
    }

    [Fact]
    public async Task A_manual_sync_tells_the_bank_the_user_is_present_only_if_every_required_header_is_available()
    {
        using var app = new BankApp();
        app.Bank.RequiredPsuHeaders = new List<string> { "Psu-User-Agent" };
        var c = await app.ConnectedAsync();
        await Sync(c);
        var read = app.Bank.Requests.Last(x => x.Path.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.Equal("BudgetTests/1.0", read.Headers["Psu-User-Agent"]);
        Assert.False(read.Headers.ContainsKey("Psu-Ip-Address"));   // pas d'adresse publique connue dans ce test

        // Une banque qui exige l'adresse IP alors qu'on ne la connaît pas : aucun en-tête, plutôt qu'une partie.
        using var strict = new BankApp();
        strict.Bank.RequiredPsuHeaders = new List<string> { "Psu-Ip-Address", "Psu-User-Agent" };
        var c2 = await strict.ConnectedAsync();
        await Sync(c2);
        var read2 = strict.Bank.Requests.Last(x => x.Path.EndsWith("/transactions", StringComparison.Ordinal));
        Assert.DoesNotContain(read2.Headers.Keys, k => k.StartsWith("Psu-", StringComparison.OrdinalIgnoreCase));
    }

    // ---------- Reconnexion et déconnexion ----------

    [Fact]
    public async Task Reconnecting_replaces_the_connection_and_keeps_links_and_history()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));
        await Sync(c);
        var oldId = (await Connection(c)).GetProperty("id").GetInt32();
        var oldSession = app.Bank.SessionId;

        // Nouvelle session : la banque donne de nouveaux identifiants de comptes, mais la même empreinte.
        app.Time.Now = app.Time.Now.AddDays(3);
        app.Bank.SessionId = "22222222-2222-4222-8222-222222222222";
        app.Bank.Accounts = new List<object>
        {
            new { account_id = new { iban = "FR7630006000011234567890189" }, details = "Compte courant", cash_account_type = "CACC", currency = "EUR", uid = "uid-perso-2", identification_hash = "hash-perso" },
        };
        app.Bank.Feed("uid-perso-2", Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"), Tx("R2", "2026-11-01", -9.90m, "PHARMACIE"));
        await Json(await c.PostAsJsonAsync("/api/bank/connect", new { name = "Banque Test", replaces = oldId }));
        var back = await c.GetAsync($"/api/bank/callback?code=def&state={app.Bank.LastState}");
        Assert.Equal("/?banque=ok#regles", back.Headers.Location?.OriginalString);

        var status = await Json(await c.GetAsync("/api/bank/status"));
        var conn = Assert.Single(status.GetProperty("connections").EnumerateArray());
        Assert.NotEqual(oldId, conn.GetProperty("id").GetInt32());
        var account = Assert.Single(conn.GetProperty("accounts").EnumerateArray());
        Assert.Equal(1, account.GetProperty("accountId").GetInt32());                    // lien retrouvé sans rien redemander
        Assert.Equal("2026-10-30", account.GetProperty("syncedThrough").GetString());    // la synchro reprend où elle en était
        Assert.Contains(app.Bank.Requests, x => x.Method == "DELETE" && x.Path == "/sessions/" + oldSession);

        var r = await Sync(c);
        Assert.Equal(1, r.GetProperty("added").GetInt32());
        Assert.Equal(2, (await Transactions(c)).Length);
    }

    [Fact]
    public async Task Disconnecting_closes_the_consent_and_keeps_the_operations()
    {
        using var app = new BankApp();
        var c = await app.ConnectedAsync();
        app.Bank.Feed("uid-perso", Tx("R1", "2026-10-29", -54.20m, "E.LECLERC LILLE"));
        await Sync(c);
        var id = (await Connection(c)).GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/bank/connections/{id}")).StatusCode);
        Assert.Contains(app.Bank.Requests, x => x.Method == "DELETE" && x.Path == "/sessions/" + app.Bank.SessionId);
        Assert.Equal(0, (await Json(await c.GetAsync("/api/bank/status"))).GetProperty("connections").GetArrayLength());
        Assert.Single(await Transactions(c));
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/bank/connections/{id}")).StatusCode);
    }
}
