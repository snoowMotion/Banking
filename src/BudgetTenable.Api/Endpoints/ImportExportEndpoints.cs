using System.Globalization;
using System.Text;
using BudgetTenable.Api.Data;
using BudgetTenable.Core.Domain;
using BudgetTenable.Core.Import;
using BudgetTenable.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace BudgetTenable.Api.Endpoints;

public static class ImportExportEndpoints
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxRows = 20_000;

    public static void MapImportExport(this IEndpointRouteBuilder api)
    {
        // Corps brut (CSV ou OFX), sans multipart : une requête cross-site ne peut pas l'envoyer sans préambule CORS.
        api.MapPost("/import", async (HttpRequest req, int accountId, BudgetDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            var account = await db.Accounts.FindAsync(new object[] { accountId }, ct);
            if (account is null) return BudgetEndpoints.Bad("Compte inconnu.");

            if (req.ContentLength > MaxBytes) return BudgetEndpoints.Bad("Fichier trop volumineux (8 Mo maximum).");
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            int n;
            while ((n = await req.Body.ReadAsync(buffer, ct)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > MaxBytes) return BudgetEndpoints.Bad("Fichier trop volumineux (8 Mo maximum).");
            }
            if (ms.Length == 0) return BudgetEndpoints.Bad("Le fichier est vide.");

            var text = StatementDecoder.Decode(ms.ToArray());
            IReadOnlyList<ParsedTransaction> rows;
            string format;
            try
            {
                var isOfx = OfxStatementParser.LooksLikeOfx(text);
                format = isOfx ? "ofx" : "csv";
                rows = isOfx ? OfxStatementParser.Parse(text) : CsvStatementParser.Parse(text);
            }
            catch (ImportFormatException e) { return BudgetEndpoints.Bad(e.Message); }
            if (rows.Count > MaxRows) return BudgetEndpoints.Bad($"Trop d'opérations ({MaxRows} maximum par fichier).");
            if (rows.Count == 0) return Results.Ok(new ImportResult(format, 0, 0, 0, 0, 0));

            var assigned = Fingerprint.Assign(rows);
            var min = rows.Min(r => r.Date);
            var max = rows.Max(r => r.Date);
            var known = (await db.Transactions.AsNoTracking()
                    .Where(t => t.AccountId == accountId && t.ProviderTransactionId != null && t.BookingDate >= min && t.BookingDate <= max)
                    .Select(t => t.ProviderTransactionId!)
                    .ToListAsync(ct)).ToHashSet();

            var rules = RuleMatcher.Ordered(await db.Rules.AsNoTracking().ToListAsync(ct));
            var now = time.GetUtcNow().UtcDateTime;
            int added = 0, duplicates = 0, categorized = 0;
            foreach (var (row, id) in assigned)
            {
                if (!known.Add(id)) { duplicates++; continue; }
                var tx = new Transaction
                {
                    AccountId = accountId,
                    ProviderTransactionId = id,
                    BookingDate = row.Date,
                    ValueDate = row.ValueDate,
                    Amount = row.Amount,
                    Currency = account.Currency,
                    Counterparty = Truncate(row.Counterparty, 300),
                    RemittanceInfo = Truncate(row.Remittance, 600),
                    CreatedAt = now,
                };
                if (RuleMatcher.Apply(tx, rules)) categorized++;
                db.Transactions.Add(tx);
                added++;
            }
            await db.SaveChangesAsync(ct);

            var toSort = await db.Transactions.CountAsync(t => t.CategoryId == null && !t.IsInternalTransfer, ct);
            return Results.Ok(new ImportResult(format, rows.Count, added, duplicates, categorized, toSort));
        });

        api.MapGet("/export.csv", async (BudgetDbContext db, CancellationToken ct) =>
        {
            var labels = await db.Categories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Label, ct);
            var txs = await db.Transactions.AsNoTracking().Include(t => t.Account).OrderBy(t => t.BookingDate).ThenBy(t => t.Id).ToListAsync(ct);

            var sb = new StringBuilder("﻿");
            sb.Append("Date;Catégorie;Contrepartie;Libellé;Note;Compte;Montant;Virement interne\r\n");
            foreach (var t in txs)
            {
                sb.Append(string.Join(';',
                    t.BookingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Text(t.CategoryId is null ? "" : labels.GetValueOrDefault(t.CategoryId, t.CategoryId)),
                    Text(t.Counterparty), Text(t.RemittanceInfo), Text(t.Note), Text(t.Account?.Label),
                    t.Amount.ToString("0.00", CultureInfo.GetCultureInfo("fr-FR")),
                    t.IsInternalTransfer ? "oui" : "non")).Append("\r\n");
            }
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", "depenses-budget.csv");
        });
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    /// <summary>Champ texte CSV entre guillemets ; neutralise les formules (=, +, -, @) interprétées par les tableurs.</summary>
    internal static string Text(string? s)
    {
        s ??= "";
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r') s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
