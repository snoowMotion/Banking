using BudgetTenable.Core.Rules;

namespace BudgetTenable.Core.Import;

/// <summary>Lecture d'un relevé CSV de banque française : séparateur, en-têtes et formats détectés.</summary>
public static class CsvStatementParser
{
    private static readonly string[] DateHeaders =
        { "DATE", "DATE OPERATION", "DATE DE L OPERATION", "DATE D OPERATION", "DATE COMPTABLE", "DATE DE COMPTABILISATION", "BOOKING DATE", "DATE OP" };
    private static readonly string[] ValueDateHeaders = { "DATE VALEUR", "DATE DE VALEUR", "VALUE DATE" };
    private static readonly string[] AmountHeaders = { "MONTANT", "AMOUNT", "MONTANT EUR", "MONTANT EN EUROS", "MONTANT EN EUR", "MONTANT DE L OPERATION" };
    private static readonly string[] DebitHeaders = { "DEBIT", "MONTANT DEBIT", "DEBIT EUR", "DEBITS" };
    private static readonly string[] CreditHeaders = { "CREDIT", "MONTANT CREDIT", "CREDIT EUR", "CREDITS" };
    private static readonly string[] LabelHeaders =
        { "LIBELLE", "LIBELLE SIMPLIFIE", "LIBELLE OPERATION", "LIBELLE DE L OPERATION", "DESCRIPTION", "INTITULE", "MOTIF", "LABEL", "DETAILS", "REFERENCE", "INFORMATIONS COMPLEMENTAIRES", "LIBELLE COMPLEMENTAIRE" };
    private static readonly string[] CounterpartyHeaders = { "BENEFICIAIRE", "TIERS", "CONTREPARTIE", "NOM", "PAYEE", "COUNTERPARTY" };

    private sealed record Columns(int Date, int? ValueDate, int? Amount, int? Debit, int? Credit, List<int> Labels, int? Counterparty);

    public static IReadOnlyList<ParsedTransaction> Parse(string content)
    {
        var rows = ReadRows(content);
        if (rows.Count == 0) throw new ImportFormatException("Le fichier est vide.");

        // L'en-tête n'est pas toujours la première ligne : certaines banques ajoutent un préambule.
        Columns? cols = null;
        var headerIndex = -1;
        for (var i = 0; i < Math.Min(rows.Count, 20); i++)
        {
            cols = Detect(rows[i]);
            if (cols is not null) { headerIndex = i; break; }
        }
        if (cols is null)
        {
            var first = string.Join(" | ", rows[0].Take(12));
            throw new ImportFormatException(
                "Colonnes non reconnues. Il faut au moins une date, un libellé et un montant (ou débit et crédit). " +
                $"En-têtes lus : {first}");
        }

        var result = new List<ParsedTransaction>();
        var line = headerIndex + 1;
        foreach (var row in rows.Skip(headerIndex + 1))
        {
            line++;
            if (row.All(string.IsNullOrWhiteSpace)) continue;
            string Cell(int? i) => i is { } k && k < row.Count ? row[k] : "";

            if (!AmountDate.TryParseDate(Cell(cols.Date), out var date))
                continue; // ligne de total, de solde ou de pied de page

            decimal amount;
            if (cols.Amount is not null)
            {
                if (!AmountDate.TryParseAmount(Cell(cols.Amount), out amount))
                    throw new ImportFormatException($"Montant illisible ligne {line} : « {Cell(cols.Amount)} ».");
            }
            else
            {
                var hasDebit = AmountDate.TryParseAmount(Cell(cols.Debit), out var debit);
                var hasCredit = AmountDate.TryParseAmount(Cell(cols.Credit), out var credit);
                if (!hasDebit && !hasCredit) continue;
                amount = (hasCredit ? credit : 0) - (hasDebit ? Math.Abs(debit) : 0);
            }

            DateOnly? valueDate = AmountDate.TryParseDate(Cell(cols.ValueDate), out var vd) ? vd : null;
            var label = AmountDate.Clean(string.Join(' ', cols.Labels.Select(i => Cell(i)).Where(x => !string.IsNullOrWhiteSpace(x))));
            result.Add(new ParsedTransaction(date, valueDate, amount, AmountDate.Clean(Cell(cols.Counterparty)), label, null));
        }
        return result;
    }

    private static Columns? Detect(List<string> header)
    {
        int? Find(string[] names)
        {
            for (var i = 0; i < header.Count; i++)
            {
                var n = Key(header[i]);
                if (names.Contains(n)) return i;
            }
            return null;
        }

        var date = Find(DateHeaders);
        if (date is null) return null;
        var amount = Find(AmountHeaders);
        var debit = Find(DebitHeaders);
        var credit = Find(CreditHeaders);
        if (amount is null && debit is null && credit is null) return null;

        var labels = new List<int>();
        for (var i = 0; i < header.Count; i++)
            if (LabelHeaders.Contains(Key(header[i]))) labels.Add(i);
        var counterparty = Find(CounterpartyHeaders);
        if (labels.Count == 0 && counterparty is null) return null;
        // Les libellés sont lus dans l'ordre des colonnes (libellé, puis complément).
        return new Columns(date.Value, Find(ValueDateHeaders), amount, debit, credit, labels, counterparty);
    }

    private static string Key(string header)
    {
        var n = TextNormalizer.Normalize(header).Replace("'", " ").Replace("(", " ").Replace(")", " ").Replace("€", " ");
        return string.Join(' ', n.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<List<string>> ReadRows(string content)
    {
        var lines = content.Split('\n');
        var probe = string.Join('\n', lines.Take(25));
        var delimiter = new[] { ';', ',', '\t' }.MaxBy(d => probe.Count(c => c == d));
        if (probe.Count(c => c == delimiter) == 0) delimiter = ';';

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == delimiter) { row.Add(field.ToString().Trim()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && i + 1 < content.Length && content[i + 1] == '\n') i++;
                row.Add(field.ToString().Trim()); field.Clear();
                rows.Add(row); row = new List<string>();
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString().Trim()); rows.Add(row); }
        return rows;
    }
}
