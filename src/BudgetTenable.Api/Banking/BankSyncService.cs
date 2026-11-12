using BudgetTenable.Api.Data;
using BudgetTenable.Api.Services;
using BudgetTenable.Core.Banking;
using BudgetTenable.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BudgetTenable.Api.Banking;

/// <summary>Une seule synchro à la fois, qu'elle soit planifiée ou lancée à la main.</summary>
public sealed class BankSyncGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed record ConnectionSyncResult(int ConnectionId, string Bank, int Added, int Reconciled, int Duplicates, int RemovedPending, string? Error);
public sealed record SyncReport(List<ConnectionSyncResult> Results, int ToSort);

public sealed class BankSyncService(
    BudgetDbContext db, IEnableBankingClient client, IClock clock, TimeProvider time,
    IOptions<EnableBankingOptions> options, BankSyncGate gate, ILogger<BankSyncService> log)
{
    /// <summary>Profondeur de la première synchro.</summary>
    public const int InitialDays = 90;
    /// <summary>
    /// Chaque synchro relit quelques jours déjà vus : une opération « en attente » y devient débitée,
    /// et une opération comptabilisée avec retard y apparaît.
    /// </summary>
    public const int OverlapDays = 10;
    private const int MaxPages = 500;

    /// <param name="psu">En-têtes de l'utilisateur présent (synchro manuelle) ; null pour la synchro planifiée.</param>
    /// <param name="onlyDue">Synchro planifiée : ne traite que les connexions dont la dernière tentative est assez ancienne.</param>
    public async Task<SyncReport> SyncAsync(PsuHeaders? psu, bool onlyDue, CancellationToken ct)
    {
        await gate.Semaphore.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            var connections = await db.BankConnections.Include(c => c.Accounts).OrderBy(c => c.Id).ToListAsync(ct);
            var results = new List<ConnectionSyncResult>();
            // Un compte joint relié par deux connexions (les deux titulaires) n'est lu qu'une fois par passage.
            var done = new HashSet<string>(StringComparer.Ordinal);

            foreach (var c in connections)
            {
                if (c.Status == BankConnectionStatus.Active && c.ValidUntil <= now)
                {
                    c.Status = BankConnectionStatus.Expired;
                    c.LastError = "Le consentement a expiré : reconnecte la banque.";
                    await db.SaveChangesAsync(ct);
                }
                if (c.Status != BankConnectionStatus.Active) continue;
                if (onlyDue && c.LastSyncAt is { } last && now - last < options.Value.SyncInterval) continue;
                results.Add(await SyncConnectionAsync(c, psu, done, now, ct));
            }

            var toSort = await db.Transactions.CountAsync(t => t.CategoryId == null && !t.IsInternalTransfer, ct);
            return new SyncReport(results, toSort);
        }
        finally { gate.Semaphore.Release(); }
    }

    private async Task<ConnectionSyncResult> SyncConnectionAsync(BankConnection c, PsuHeaders? psu, HashSet<string> done, DateTime now, CancellationToken ct)
    {
        int added = 0, reconciled = 0, duplicates = 0, removed = 0;
        string? error = null;

        // Soit tous les en-têtes exigés par la banque, soit aucun (la synchro compte alors comme automatique).
        var required = (c.RequiredPsuHeaders ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var usePsu = psu is not null && psu.Values.Count > 0 && psu.Covers(required) ? psu : null;

        try
        {
            foreach (var link in c.Accounts.Where(a => a.AccountId is not null).OrderBy(a => a.Id))
            {
                if (!done.Add(link.IdentificationHash)) continue;
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == link.AccountId, ct);
                if (account is null) continue;

                var today = clock.Today;
                var first = link.SyncedThrough is null;
                var from = (link.SyncedThrough ?? today).AddDays(first ? -InitialDays : -OverlapDays);

                var (transactions, withPsu) = await FetchAsync(link.ProviderUid, from, first, usePsu, ct);
                usePsu = withPsu;

                var outcome = await TransactionIngestor.IngestAsync(db, account, BankFeed.Assign(transactions), now, bankSync: true, ct);

                // Une opération « en attente » venue de la banque et absente de la relecture a été annulée, ou débitée
                // pour un autre montant (pré-autorisation de carburant, pourboire) : la garder compterait la dépense deux fois.
                var pending = await db.Transactions
                    .Where(t => t.AccountId == account.Id && t.Status == TransactionStatus.Pending && t.BookingDate >= from && t.ProviderTransactionId != null)
                    .ToListAsync(ct);
                // Refiltré en mémoire : une opération tout juste rapprochée est encore « en attente » en base.
                var stale = pending.Where(t =>
                    t.Status == TransactionStatus.Pending && BankFeed.IsFromSync(t.ProviderTransactionId) && !outcome.Seen.Contains(t.Id)).ToList();
                db.Transactions.RemoveRange(stale);

                link.SyncedThrough = today;
                await db.SaveChangesAsync(ct);

                added += outcome.Added; reconciled += outcome.Reconciled; duplicates += outcome.Duplicates; removed += stale.Count;
            }
            c.LastSuccessAt = now;
            c.LastError = null;
        }
        catch (EnableBankingException e)
        {
            DiscardUnsavedTransactions();
            error = Describe(e);
            if (e.Error is "EXPIRED_SESSION" or "CLOSED_SESSION" or "SESSION_DOES_NOT_EXIST" or "WRONG_SESSION_STATUS") c.Status = BankConnectionStatus.Expired;
            else if (e.Error is "REVOKED_SESSION") c.Status = BankConnectionStatus.Revoked;
            c.LastError = error;
            // Ni montant ni numéro de compte dans les journaux : seulement le code d'erreur.
            log.LogWarning("Synchro bancaire : échec pour la connexion {ConnectionId} ({Status}, {Code}).", c.Id, (int)e.Status, e.Error ?? "sans code");
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            DiscardUnsavedTransactions();
            error = "Enable Banking est injoignable pour l'instant. Nouvel essai à la prochaine synchro.";
            c.LastError = error;
            log.LogWarning("Synchro bancaire : agrégateur injoignable pour la connexion {ConnectionId}.", c.Id);
        }

        c.LastSyncAt = now;
        await db.SaveChangesAsync(ct);
        if (error is null)
            log.LogInformation("Synchro bancaire : connexion {ConnectionId}, {Added} ajoutées, {Reconciled} rapprochées, {Removed} en attente retirées.", c.Id, added, reconciled, removed);
        return new ConnectionSyncResult(c.Id, c.AspspName, added, reconciled, duplicates, removed, error);
    }

    /// <summary>Après un échec en cours de compte, rien de ce compte n'est enregistré à moitié.</summary>
    private void DiscardUnsavedTransactions()
    {
        foreach (var entry in db.ChangeTracker.Entries<Transaction>().ToList()) entry.State = EntityState.Detached;
    }

    /// <summary>
    /// Lit toutes les pages. Une page vide accompagnée d'une clé de continuation n'est pas la fin : il faut continuer
    /// tant qu'une clé est renvoyée. Renvoie aussi les en-têtes PSU encore utilisables (aucun si la banque les a refusés).
    /// </summary>
    private async Task<(List<EbTransaction>, PsuHeaders?)> FetchAsync(string uid, DateOnly from, bool first, PsuHeaders? psu, CancellationToken ct)
    {
        // « longest » : la banque remonte aussi loin qu'elle le permet (première synchro). « default » : relecture récente.
        var strategy = first ? "longest" : "default";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var all = new List<EbTransaction>();
                string? key = null;
                for (var page = 0; page < MaxPages; page++)
                {
                    var p = await client.GetTransactionsAsync(uid, from, strategy, key, psu, ct);
                    if (p.Transactions is not null) all.AddRange(p.Transactions);
                    key = p.ContinuationKey;
                    if (string.IsNullOrEmpty(key)) return (all, psu);
                }
                throw new EnableBankingException(System.Net.HttpStatusCode.BadGateway, "TOO_MANY_PAGES", "Trop de pages renvoyées par la banque.");
            }
            catch (EnableBankingException e) when (attempt < 2 && e.Error == "WRONG_TRANSACTIONS_PERIOD" && strategy == "default")
            {
                strategy = "longest";   // la période demandée n'est plus disponible : prendre ce que la banque donne
            }
            catch (EnableBankingException e) when (attempt < 2 && psu is not null && e.Error is "PSU_HEADER_NOT_PROVIDED" or "PSU_HEADER_INVALID")
            {
                psu = null;             // en-têtes refusés : la synchro passe en mode automatique
            }
        }
    }

    private static string Describe(EnableBankingException e) => e.Error switch
    {
        "EXPIRED_SESSION" or "CLOSED_SESSION" or "SESSION_DOES_NOT_EXIST" or "WRONG_SESSION_STATUS" => "Le consentement a expiré : reconnecte la banque.",
        "REVOKED_SESSION" => "Le consentement a été révoqué : reconnecte la banque.",
        "ASPSP_RATE_LIMIT_EXCEEDED" => "La banque limite le nombre de lectures automatiques (souvent quatre par jour). Nouvel essai à la prochaine synchro.",
        "ASPSP_ERROR" or "ASPSP_TIMEOUT" => "La banque ne répond pas correctement pour l'instant. Nouvel essai à la prochaine synchro.",
        "ASPSP_PSU_ACTION_REQUIRED" => "La banque attend une action de ta part : connecte-toi à son site, puis reconnecte la banque ici si besoin.",
        "NO_ACCOUNTS_ADDED" or "ASPSP_ACCOUNT_NOT_ACCESSIBLE" or "ACCOUNT_DOES_NOT_EXIST" => "Ce compte n'est pas (ou plus) lié à l'application dans le portail Enable Banking.",
        "UNAUTHORIZED_ACCESS" or "AUTHORIZATION_NOT_PROVIDED" => "Enable Banking refuse la clé de l'application : vérifie BUDGET_EB_APP_ID et la clé privée.",
        _ when (int)e.Status == 429 => "Trop de requêtes pour l'instant. Nouvel essai à la prochaine synchro.",
        _ => $"Erreur de l'agrégateur ({e.Error ?? ((int)e.Status).ToString()}). Nouvel essai à la prochaine synchro.",
    };
}

/// <summary>Synchro planifiée : vérifie toutes les dix minutes si une connexion est due.</summary>
public sealed class BankSyncWorker(IServiceScopeFactory scopes, IOptions<EnableBankingOptions> options, ILogger<BankSyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Configured || o.SyncHours <= 0) return;
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<BankSyncService>().SyncAsync(null, onlyDue: true, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning("Synchro bancaire planifiée : erreur inattendue ({Type}).", e.GetType().Name);
                }
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* arrêt de l'application */ }
    }
}
