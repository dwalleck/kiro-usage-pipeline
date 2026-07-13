using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace KiroIngest;

// Thin seam over CsvHelper's low-level parser. Returning (header, rows) keeps the
// transform decoupled from the CSV library (and easy to unit-test) while delegating
// quoting, escaped quotes, and embedded newlines to a battle-tested reader.
public static class Csv
{
    private static readonly CsvConfiguration Config = new(CultureInfo.InvariantCulture)
    {
        // The dynamic <model>_messages columns mean row width varies file to file,
        // so don't enforce a constant field count.
        BadDataFound = null,
        IgnoreBlankLines = true,
        DetectColumnCountChanges = false,
    };

    public static (string[] Header, List<string[]> Rows) Parse(string text)
    {
        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, Config);

        string[]? header = null;
        var rows = new List<string[]>();

        while (parser.Read())
        {
            var record = parser.Record;
            if (record is null)
            {
                continue;
            }

            if (header is null)
            {
                header = record;
            }
            else
            {
                rows.Add(record);
            }
        }

        return (header ?? [], rows);
    }
}
