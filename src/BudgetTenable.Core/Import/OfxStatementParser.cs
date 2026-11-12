using System.Text.RegularExpressions;

namespace BudgetTenable.Core.Import;

/// <summary>Lecture OFX, en SGML (v1, balises non fermées) comme en XML (v2).</summary>
public static partial class OfxStatementParser
{
    [GeneratedRegex(@"<STMTTRN>(.*?)(?:</STMTTRN>|(?=<STMTTRN>)|(?=</BANKTRANLIST>)|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Block();

    public static bool LooksLikeOfx(string content) =>
        content.Contains("<OFX", StringComparison.OrdinalIgnoreCase) || content.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<ParsedTransaction> Parse(string content)
    {
        var result = new List<ParsedTransaction>();
        foreach (Match m in Block().Matches(content))
        {
            var body = m.Groups[1].Value;
            var posted = Tag(body, "DTPOSTED");
            var amountText = Tag(body, "TRNAMT");
            if (posted is null || amountText is null) continue;

            if (!AmountDate.TryParseDate(posted.Length >= 8 ? posted[..8] : posted, out var date))
                throw new ImportFormatException($"Date OFX illisible : « {posted} ».");
            if (!AmountDate.TryParseAmount(amountText, out var amount))
                throw new ImportFormatException($"Montant OFX illisible : « {amountText} ».");

            DateOnly? value = Tag(body, "DTUSER") is { Length: >= 8 } u && AmountDate.TryParseDate(u[..8], out var vd) ? vd : null;
            result.Add(new ParsedTransaction(
                date, value, amount,
                AmountDate.Clean(Decode(Tag(body, "NAME"))),
                AmountDate.Clean(Decode(Tag(body, "MEMO"))),
                AmountDate.Clean(Tag(body, "FITID"))));
        }
        if (result.Count == 0 && !LooksLikeOfx(content))
            throw new ImportFormatException("Aucune opération OFX trouvée dans le fichier.");
        return result;
    }

    private static string? Tag(string block, string name)
    {
        var m = Regex.Match(block, $@"<{name}>\s*([^<\r\n]*)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static string? Decode(string? s) => s is null ? null : System.Net.WebUtility.HtmlDecode(s);
}
