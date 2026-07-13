using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace KiroIngest;

// Thin seam over CsvHelper's low-level parser. Dynamic model columns vary
// between files, but every row within one file must match that file's header.
public static class Csv
{
    private static readonly CsvConfiguration Config = new(CultureInfo.InvariantCulture)
    {
        IgnoreBlankLines = true,
        DetectColumnCountChanges = true,
    };

    public static (string[] Header, List<string[]> Rows) Parse(string text)
    {
        using var reader = new StringReader(text);
        using var parser = new CsvParser(reader, Config);

        string[]? header = null;
        var rows = new List<string[]>();

        try
        {
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
                    if (record.Length != header.Length)
                    {
                        throw new InvalidDataException(
                            $"User Activity Report row has {record.Length} columns; expected {header.Length}");
                    }

                    rows.Add(record);
                }
            }
        }
        catch (CsvHelperException ex)
        {
            throw new InvalidDataException(
                $"Malformed User Activity Report CSV: {ex.Message}",
                ex);
        }

        return (header ?? [], rows);
    }
}
