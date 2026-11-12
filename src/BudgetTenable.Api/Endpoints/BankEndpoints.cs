using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using BudgetTenable.Api.Banking;
using BudgetTenable.Api.Data;
using BudgetTenable.Core.Banking;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTenable.Api.Endpoints;

public sealed record BankAccountLinkDto(int Id, string Label, string? IbanMasked, string Currency, int? AccountId, DateOnly? SyncedThrough);
public sealed record BankConnectionDto(
    int Id, string Bank, string Country, string Status, DateTime ValidUntil, int DaysLeft, bool ExpiresSoon,
    DateTime? LastSyncAt, DateTime? LastSuccessAt, string? LastError, List<BankAccountLinkDto> Accounts);
public sealed record BankStatusDto(bool Configured, double SyncHours, string Country, List<BankConnectionDto> Connections);
public sealed record BankDto(string Name, string Country, int MaxConsentDays, bool Beta);
public sealed record BankConnectRequest(string? Name, string? Country, int? Replaces);
public sealed record BankConnectResponse(string Url);
public sealed record BankLinkRequest(int? AccountId, bool? Create);

public static class BankEndpoints
{
    public const string CallbackPath = "/api/bank/callback";
    /// <summary>Nombre de jours avant la fin du consentement à partir duquel l'interface prévient.</summary>
    public const int WarnDays = 14;
    /// <summary>Durée laissée pour s'authentifier sur le site de la banque.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaxConsent = TimeSpan.FromDays(180);
    private static readonly TimeSpan FallbackConsent = TimeSpan.FromDays(90);

    public static void MapBank(this IEndpointRouteBuilder api)
    {
        var g = api.MapGroup("/bank");

        g.MapGet("/status", async (BudgetDbContext db, IOptions<EnableBankingOptions> options, EnableBankingAuth auth, TimeProvider time, CancellationToken ct) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var connections = await db.BankConnections.AsNoTracking().Include(c => c.Accounts).OrderBy(c => c.Id).ToListAsync(ct);
            return new BankStatusDto(auth.Configured, options.Value.SyncHours, options.Value.Country, connections.Select(c => ToDto(c, now)).ToList());
        });

        g.MapGet("/aspsps", async (IEnableBankingClient client, EnableBankingAuth auth, IOptions<EnableBankingOptions> options, CancellationToken ct) =>
        {
            if (!auth.Configured) return NotConfigured();
            try
            {
                var list = await client.GetAspspsAsync(options.Value.Country, ct);
                return Results.Ok(list.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Country))
                    .Select(a => new BankDto(a.Name!, a.Country!, (int)(Consent(a).TotalDays), a.Beta == true))
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList());
            }
            catch (Exception e) when (IsUpstream(e, ct)) { return Upstream(e); }
        });

        g.MapPost("/connect", async (BankConnectRequest req, BudgetDbContext db, IEnableBankingClient client, EnableBankingAuth auth,
            IOptions<EnableBankingOptions> options, TimeProvider time, CancellationToken ct) =>
        {
            if (!auth.Configured) return NotConfigured();
            if (string.IsNullOrWhiteSpace(req.Name)) return BudgetEndpoints.Bad("Choisis une banque.");
            var country = string.IsNullOrWhiteSpace(req.Country) ? options.Value.Country : req.Country!.Trim().ToUpperInvariant();
            if (req.Replaces is { } old && !await db.BankConnections.AnyAsync(c => c.Id == old, ct)) return BudgetEndpoints.Bad("Connexion à remplacer inconnue.");

            try
            {
                // La durée maximale du consentement dépend de la banque : elle est lue dans la liste, jamais supposée.
                var aspsp = (await client.GetAspspsAsync(country, ct)).FirstOrDefault(a =>
                    string.Equals(a.Name, req.Name.Trim(), StringComparison.OrdinalIgnoreCase) && string.Equals(a.Country, country, StringComparison.OrdinalIgnoreCase));
                if (aspsp is null) return BudgetEndpoints.Bad("Banque inconnue d'Enable Banking pour ce pays.");

                var now = time.GetUtcNow();
                var validUntil = now.Add(Consent(aspsp)).AddMinutes(-5);
                var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

                var response = await client.StartAuthorizationAsync(new EbStartAuthRequest(
                    new EbAccess(validUntil.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture), Balances: true, Transactions: true),
                    new EbAspspRef(aspsp.Name!, aspsp.Country!), state, options.Value.RedirectUrl!, "personal", "fr"), ct);
                if (!Uri.TryCreate(response.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                    return Results.Json(new { error = "Enable Banking n'a pas renvoyé d'adresse d'autorisation valide." }, statusCode: StatusCodes.Status502BadGateway);

                var cutoff = now.UtcDateTime - StateLifetime;
                db.BankAuthStates.RemoveRange(await db.BankAuthStates.Where(s => s.CreatedAt < cutoff).ToListAsync(ct));
                db.BankAuthStates.Add(new BankAuthState
                {
                    State = state, AspspName = aspsp.Name!, AspspCountry = aspsp.Country!.ToUpperInvariant(), ValidUntil = validUntil.UtcDateTime,
                    RequiredPsuHeaders = aspsp.RequiredPsuHeaders is { Count: > 0 } h ? string.Join(',', h) : null,
                    CreatedAt = now.UtcDateTime, ReplacesConnectionId = req.Replaces,
                });
                await db.SaveChangesAsync(ct);
                return Results.Ok(new BankConnectResponse(url.ToString()));
            }
            catch (Exception e) when (IsUpstream(e, ct)) { return Upstream(e); }
        });

        // Seul endpoint public de l'API : le navigateur y revient depuis le site de la banque. Il n'agit que sur
        // présentation d'un jeton « state » créé par un utilisateur connecté, à usage unique et valable une heure.
        g.MapGet("/callback", async (string? code, string? state, string? error, BudgetDbContext db, IEnableBankingClient client,
            TimeProvider time, ILoggerFactory logs, CancellationToken ct) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var pending = string.IsNullOrEmpty(state) || state.Length > 64 ? null : await db.BankAuthStates.FirstOrDefaultAsync(s => s.State == state, ct);
            if (pending is null) return Back("invalide");
            db.BankAuthStates.Remove(pending);
            await db.SaveChangesAsync(ct);
            if (now - pending.CreatedAt > StateLifetime) return Back("expire");
            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code)) return Back(error == "access_denied" ? "refuse" : "erreur");

            EbSession session;
            try { session = await client.AuthorizeSessionAsync(code, ct); }
            catch (Exception e) when (IsUpstream(e, ct))
            {
                logs.CreateLogger("BankCallback").LogWarning("Connexion bancaire : échange du code refusé ({Code}).", (e as EnableBankingException)?.Error ?? e.GetType().Name);
                return Back((e as EnableBankingException)?.Error == "NO_ACCOUNTS_ADDED" ? "aucun-compte" : "erreur");
            }
            if (string.IsNullOrEmpty(session.SessionId)) return Back("erreur");

            var validUntil = DateTimeOffset.TryParse(session.Access?.ValidUntil, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var v) ? v.UtcDateTime : pending.ValidUntil;
            var connection = new BankConnection
            {
                SessionId = session.SessionId, AspspName = pending.AspspName, AspspCountry = pending.AspspCountry, ValidUntil = validUntil,
                RequiredPsuHeaders = pending.RequiredPsuHeaders, CreatedAt = now,
            };
            var replaced = pending.ReplacesConnectionId is { } rid
                ? await db.BankConnections.Include(c => c.Accounts).FirstOrDefaultAsync(c => c.Id == rid, ct) : null;
            var accounts = await db.Accounts.ToListAsync(ct);

            foreach (var a in session.Accounts ?? new List<EbAccount>())
            {
                // Sans « uid », la banque ne permet pas de lire ce compte (bloqué, clôturé).
                if (string.IsNullOrEmpty(a.Uid) || string.IsNullOrEmpty(a.IdentificationHash)) continue;
                var previous = replaced?.Accounts.FirstOrDefault(x => x.IdentificationHash == a.IdentificationHash);
                var masked = BankFeed.MaskIban(a.AccountId?.Iban);
                connection.Accounts.Add(new BankAccountLink
                {
                    ProviderUid = a.Uid, IdentificationHash = a.IdentificationHash,
                    Label = Truncate(FirstNonEmpty(a.Details, a.Product, a.Name, masked, "Compte"), 120),
                    IbanMasked = masked, Currency = Truncate(a.Currency ?? "EUR", 3),
                    // Un compte déjà relié (reconnexion) retrouve son compte de l'appli et sa date de reprise.
                    AccountId = previous?.AccountId ?? accounts.FirstOrDefault(x => x.ProviderAccountId == a.IdentificationHash)?.Id,
                    SyncedThrough = previous?.SyncedThrough,
                });
            }

            db.BankConnections.Add(connection);
            if (replaced is not null) db.BankConnections.Remove(replaced);
            await db.SaveChangesAsync(ct);

            if (replaced is not null)
            {
                // L'ancien consentement est fermé chez l'agrégateur ; un échec ici n'empêche rien.
                try { await client.DeleteSessionAsync(replaced.SessionId, ct); }
                catch (Exception e) when (IsUpstream(e, ct)) { }
            }
            return Back(connection.Accounts.Count == 0 ? "aucun-compte" : "ok");
        }).RequireRateLimiting("bank-callback");

        g.MapPut("/accounts/{id:int}", async (int id, BankLinkRequest req, BudgetDbContext db, CancellationToken ct) =>
        {
            var link = await db.BankAccountLinks.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (link is null) return Results.NotFound(new { error = "Compte bancaire inconnu." });

            if (req.Create == true)
            {
                var created = new Account { Label = Truncate(link.Label, 80), Type = AccountType.Perso, Currency = link.Currency, ProviderAccountId = link.IdentificationHash, IbanMasked = link.IbanMasked };
                db.Accounts.Add(created);
                await db.SaveChangesAsync(ct);
                link.AccountId = created.Id;
            }
            else if (req.AccountId is { } accountId)
            {
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");
                // Les références d'opérations ne sont uniques que par compte bancaire : deux comptes bancaires
                // différents ne doivent pas alimenter le même compte de l'appli.
                if (account.ProviderAccountId is not null && account.ProviderAccountId != link.IdentificationHash)
                    return BudgetEndpoints.Bad("Ce compte de l'appli est déjà relié à un autre compte bancaire.");
                account.ProviderAccountId = link.IdentificationHash;
                account.IbanMasked = link.IbanMasked;
                link.AccountId = account.Id;
            }
            else
            {
                link.AccountId = null;
                var stillLinked = await db.BankAccountLinks.AnyAsync(l => l.Id != link.Id && l.IdentificationHash == link.IdentificationHash && l.AccountId != null, ct);
                if (!stillLinked)
                    foreach (var a in await db.Accounts.Where(a => a.ProviderAccountId == link.IdentificationHash).ToListAsync(ct)) a.ProviderAccountId = null;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new BankAccountLinkDto(link.Id, link.Label, link.IbanMasked, link.Currency, link.AccountId, link.SyncedThrough));
        });

        g.MapPost("/sync", async (HttpContext ctx, BankSyncService sync, EnableBankingAuth auth, CancellationToken ct) =>
        {
            if (!auth.Configured) return NotConfigured();
            return Results.Ok(await sync.SyncAsync(PsuFrom(ctx), onlyDue: false, ct));
        });

        g.MapDelete("/connections/{id:int}", async (int id, BudgetDbContext db, IEnableBankingClient client, EnableBankingAuth auth, CancellationToken ct) =>
        {
            var c = await db.BankConnections.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Connexion inconnue." });
            if (auth.Configured)
            {
                // Ferme le consentement chez l'agrégateur ; s'il est déjà expiré ou injoignable, on retire quand même la connexion.
                try { await client.DeleteSessionAsync(c.SessionId, ct); }
                catch (Exception e) when (IsUpstream(e, ct)) { }
            }
            db.BankConnections.Remove(c);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    private static BankConnectionDto ToDto(BankConnection c, DateTime now)
    {
        var expired = c.Status == BankConnectionStatus.Active && c.ValidUntil <= now;
        var status = expired ? BankConnectionStatus.Expired : c.Status;
        var daysLeft = Math.Max(0, (int)Math.Floor((c.ValidUntil - now).TotalDays));
        return new BankConnectionDto(c.Id, c.AspspName, c.AspspCountry, status.ToString().ToLowerInvariant(),
            DateTime.SpecifyKind(c.ValidUntil, DateTimeKind.Utc), daysLeft, status == BankConnectionStatus.Active && daysLeft <= WarnDays,
            Utc(c.LastSyncAt), Utc(c.LastSuccessAt), expired ? "Le consentement a expiré : reconnecte la banque." : c.LastError,
            c.Accounts.OrderBy(a => a.Id).Select(a => new BankAccountLinkDto(a.Id, a.Label, a.IbanMasked, a.Currency, a.AccountId, a.SyncedThrough)).ToList());
    }

    private static DateTime? Utc(DateTime? d) => d is null ? null : DateTime.SpecifyKind(d.Value, DateTimeKind.Utc);

    /// <summary>Durée du consentement demandée : le maximum de la banque, plafonné à 180 jours.</summary>
    private static TimeSpan Consent(EbAspsp a)
    {
        var max = a.MaximumConsentValidity is > 0 and var s ? TimeSpan.FromSeconds(s) : FallbackConsent;
        return max > MaxConsent ? MaxConsent : max;
    }

    /// <summary>Retour vers l'onglet « Règles et import », avec le résultat à afficher.</summary>
    private static IResult Back(string outcome) => Results.Redirect("/?banque=" + outcome + "#regles");

    private static IResult NotConfigured() => BudgetEndpoints.Bad("La synchronisation bancaire n'est pas configurée (voir le README).");

    private static bool IsUpstream(Exception e, CancellationToken ct) =>
        e is EnableBankingException or HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested);

    private static IResult Upstream(Exception e) => Results.Json(
        new { error = e is EnableBankingException eb ? $"Enable Banking a refusé la demande ({eb.Error ?? ((int)eb.Status).ToString()})." : "Enable Banking est injoignable pour l'instant." },
        statusCode: StatusCodes.Status502BadGateway);

    /// <summary>
    /// En-têtes de l'utilisateur présent, transmis à la banque par l'agrégateur. L'adresse IP n'est envoyée que si elle
    /// est publique : derrière un proxy sans BUDGET_TRUST_PROXY, ou sur le réseau local, elle ne dirait rien à la banque.
    /// </summary>
    private static PsuHeaders PsuFrom(HttpContext ctx)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is not null && IsPublic(ip)) values["Psu-Ip-Address"] = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        void Copy(string psu, string header)
        {
            var value = ctx.Request.Headers[header].ToString();
            if (!string.IsNullOrWhiteSpace(value)) values[psu] = value.Length <= 500 ? value : value[..500];
        }
        Copy("Psu-User-Agent", "User-Agent");
        Copy("Psu-Accept", "Accept");
        Copy("Psu-Accept-Charset", "Accept-Charset");
        Copy("Psu-Accept-Encoding", "Accept-Encoding");
        Copy("Psu-Accept-language", "Accept-Language");
        return new PsuHeaders(values);
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 0);
        }
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal);
    }

    private static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!.Trim();
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
