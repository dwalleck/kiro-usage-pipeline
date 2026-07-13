using KiroIngest;

namespace KiroIngest.Tests;

public class CsvTests
{
    [Test]
    public async Task Parse_StandardCsv_ReturnsHeaderAndRows()
    {
        var (header, rows) = Csv.Parse("a,b,c\n1,2,3\n4,5,6");

        await Assert.That(header).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[1]).IsEquivalentTo(new[] { "4", "5", "6" });
    }

    [Test]
    public async Task Parse_QuotedField_StripsQuotes()
    {
        var (_, rows) = Csv.Parse("email\n\"dwalleck@proton.me\"");

        await Assert.That(rows[0][0]).IsEqualTo("dwalleck@proton.me");
    }

    [Test]
    public async Task Parse_EmbeddedCommas_PreservesContent()
    {
        var (_, rows) = Csv.Parse("a,b\n\"has,comma\",tail");

        await Assert.That(rows[0][0]).IsEqualTo("has,comma");
        await Assert.That(rows[0][1]).IsEqualTo("tail");
    }

    [Test]
    public async Task Parse_EscapedQuotes_UnescapesToSingleQuote()
    {
        var (_, rows) = Csv.Parse("a\n\"she said \"\"hi\"\"\"");

        await Assert.That(rows[0][0]).IsEqualTo("she said \"hi\"");
    }

    [Test]
    public async Task Parse_EmptyInput_ReturnsEmptyHeaderAndRows()
    {
        var (header, rows) = Csv.Parse("");

        await Assert.That(header).IsEmpty();
        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task Parse_BlankLines_Skips()
    {
        var (_, rows) = Csv.Parse("a,b\n1,2\n\n3,4\n");

        await Assert.That(rows.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_RowWidthDiffersFromHeader_Throws()
    {
        await Assert.That(() => Csv.Parse("a,b,c\n1,2"))
            .Throws<InvalidDataException>();
    }

    [Test]
    public async Task Parse_BadQuotedData_Throws()
    {
        await Assert.That(() => Csv.Parse("a,b\n\"value\"junk,tail"))
            .Throws<InvalidDataException>();
    }
}
