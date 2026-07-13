using KiroIngest;

namespace KiroIngest.Tests;

public class CsvTests
{
    [Test]
    public async Task Parses_header_and_rows()
    {
        var (header, rows) = Csv.Parse("a,b,c\n1,2,3\n4,5,6");

        await Assert.That(header).IsEquivalentTo(new[] { "a", "b", "c" });
        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows[1]).IsEquivalentTo(new[] { "4", "5", "6" });
    }

    [Test]
    public async Task Strips_surrounding_double_quotes()
    {
        var (_, rows) = Csv.Parse("email\n\"dwalleck@proton.me\"");

        await Assert.That(rows[0][0]).IsEqualTo("dwalleck@proton.me");
    }

    [Test]
    public async Task Preserves_commas_inside_quoted_fields()
    {
        var (_, rows) = Csv.Parse("a,b\n\"has,comma\",tail");

        await Assert.That(rows[0][0]).IsEqualTo("has,comma");
        await Assert.That(rows[0][1]).IsEqualTo("tail");
    }

    [Test]
    public async Task Handles_doubled_quote_escapes()
    {
        var (_, rows) = Csv.Parse("a\n\"she said \"\"hi\"\"\"");

        await Assert.That(rows[0][0]).IsEqualTo("she said \"hi\"");
    }

    [Test]
    public async Task Skips_blank_lines()
    {
        var (_, rows) = Csv.Parse("a,b\n1,2\n\n3,4\n");

        await Assert.That(rows.Count).IsEqualTo(2);
    }
}
