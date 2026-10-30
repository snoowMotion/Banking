using System.Text;
using BudgetTenable.Core.Import;

namespace BudgetTenable.Tests;

public class ImportParserTests
{
    private static DateOnly D(string s) => DateOnly.Parse(s);

    [Theory]
    [InlineData("12,50", 12.50)]
    [InlineData("-12,50", -12.50)]
    [InlineData("1 234,56", 1234.56)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("-1 234,56 €", -1234.56)]
    [InlineData("(45,00)", -45.00)]
    [InlineData("+7.5", 7.5)]
    [InlineData("3", 3)]
    public void Amounts(string input, double expected)
    {
        Assert.True(AmountDate.TryParseAmount(input, out var v));
        Assert.Equal((decimal)expected, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12,5,6x")]
    public void Bad_amounts(string input) => Assert.False(AmountDate.TryParseAmount(input, out _));

    [Theory]
    [InlineData("28/10/2026", "2026-10-28")]
    [InlineData("28-10-2026", "2026-10-28")]
    [InlineData("2026-10-28", "2026-10-28")]
    [InlineData("2026-10-28T23:59:59+02:00", "2026-10-28")]
    [InlineData("28/10/26", "2026-10-28")]
    public void Dates(string input, string expected)
    {
        Assert.True(AmountDate.TryParseDate(input, out var d));
        Assert.Equal(D(expected), d);
    }

    [Fact]
    public void Csv_with_signed_amount_column_and_semicolons()
    {
        var rows = CsvStatementParser.Parse(
            "Date opération;Libellé;Montant\r\n" +
            "28/10/2026;\"CB E.LECLERC; LILLE\";-85,40\r\n" +
            "29/10/2026;VIR CAF;198,00\r\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(new ParsedTransaction(D("2026-10-28"), null, -85.40m, null, "CB E.LECLERC; LILLE", null), rows[0]);
        Assert.Equal(198m, rows[1].Amount);
    }

    [Fact]
    public void Csv_with_debit_and_credit_columns_and_a_preamble()
    {
        var rows = CsvStatementParser.Parse(
            "Compte : 0001234;;;\n" +
            "Période du 01/10/2026 au 31/10/2026;;;\n" +
            "\n" +
            "Date;Libellé;Débit;Crédit\n" +
            "01/10/2026;PRLV FREE MOBILE;12,99;\n" +
            "02/10/2026;VIR SALAIRE;;2 050,00\n" +
            "Total;;12,99;2 050,00\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal(-12.99m, rows[0].Amount);
        Assert.Equal(2050m, rows[1].Amount);
    }

    [Fact]
    public void Csv_with_commas_and_counterparty_column()
    {
        var rows = CsvStatementParser.Parse("date,description,payee,amount\n2026-10-30,Plein,TOTAL ENERGIES,-62.10\n");
        Assert.Equal("TOTAL ENERGIES", rows.Single().Counterparty);
        Assert.Equal("Plein", rows.Single().Remittance);
        Assert.Equal(-62.10m, rows.Single().Amount);
    }

    [Fact]
    public void Unknown_columns_give_a_helpful_error()
    {
        var e = Assert.Throws<ImportFormatException>(() => CsvStatementParser.Parse("a;b;c\n1;2;3\n"));
        Assert.Contains("a | b | c", e.Message);
    }

    [Fact]
    public void Latin1_files_are_decoded()
    {
        var bytes = Encoding.Latin1.GetBytes("Date;Libellé;Montant\n28/10/2026;PRÉLÈVEMENT;-5,00\n");
        var rows = CsvStatementParser.Parse(StatementDecoder.Decode(bytes));
        Assert.Equal("PRÉLÈVEMENT", rows.Single().Remittance);
    }

    [Fact]
    public void Ofx_sgml_and_xml()
    {
        const string sgml = """
            OFXHEADER:100
            <OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKTRANLIST>
            <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20261028120000[0:GMT]<TRNAMT>-85.40<FITID>A1<NAME>E.LECLERC<MEMO>CB LILLE
            <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20261029<TRNAMT>198,00<FITID>A2<NAME>CAF
            </BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>
            """;
        var rows = OfxStatementParser.Parse(sgml);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new ParsedTransaction(D("2026-10-28"), null, -85.40m, "E.LECLERC", "CB LILLE", "A1"), rows[0]);
        Assert.Equal(198m, rows[1].Amount);

        const string xml = "<OFX><STMTTRN><DTPOSTED>20261030</DTPOSTED><TRNAMT>-9.99</TRNAMT><FITID>X9</FITID><NAME>Ring &amp; Co</NAME></STMTTRN></OFX>";
        var x = OfxStatementParser.Parse(xml).Single();
        Assert.Equal("Ring & Co", x.Counterparty);
        Assert.Equal("X9", x.ProviderId);
    }

    [Fact]
    public void Fingerprints_are_stable_and_distinguish_identical_operations_of_the_same_day()
    {
        var a = new ParsedTransaction(D("2026-10-28"), null, -2.50m, null, "CAFE", null);
        var first = Fingerprint.Assign(new[] { a, a });
        var again = Fingerprint.Assign(new[] { a, a });
        Assert.NotEqual(first[0].Id, first[1].Id);
        Assert.Equal(first.Select(x => x.Id), again.Select(x => x.Id));
        Assert.Equal("A1", Fingerprint.Assign(new[] { a with { ProviderId = "A1" } })[0].Id);
    }
}
