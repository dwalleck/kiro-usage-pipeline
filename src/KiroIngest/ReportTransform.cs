using System.Globalization;
using System.Text.RegularExpressions;

namespace KiroIngest;

// Pure transform: User Activity Report CSV text + Target List -> the two facts,
// grouped by (date, client_type) partition. No AWS or I/O dependencies, so it is
// fully unit-testable. Live and backfill paths share this exact logic.
public static partial class ReportTransform
{
    // Static source columns, exact casing as emitted by Kiro (see ticket 01 findings).
    private const string ColDate = "Date";
    private const string ColUserId = "UserId";
    private const string ColClientType = "Client_Type";
    private const string ColChatConversations = "Chat_Conversations";
    private const string ColCreditsUsed = "Credits_Used";
    private const string ColOverageCap = "Overage_Cap";
    private const string ColOverageCreditsUsed = "Overage_Credits_Used";
    private const string ColOverageEnabled = "Overage_Enabled";
    private const string ColProfileId = "ProfileId";
    private const string ColSubscriptionTier = "Subscription_Tier";
    private const string ColTotalMessages = "Total_Messages";
    private const string ColNewUser = "New_User";
    private const string ColUserEmail = "User_Email";

    private const string ModelSuffix = "_messages";

    // Dynamic model columns are all-lowercase "<model>_messages" (dots allowed, e.g.
    // claude_opus_4.8_messages). The static Total_Messages column is capitalized so
    // this case-sensitive pattern never matches it.
    [GeneratedRegex("^[a-z0-9._]+_messages$")]
    private static partial Regex ModelColumnPattern();

    public static TransformResult Transform(string csvText, ISet<string> targetEmails)
    {
        var (header, rows) = Csv.Parse(csvText);
        if (header.Length == 0)
        {
            return new TransformResult { RowsRead = 0, RowsKept = 0, Partitions = [] };
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Length; i++)
        {
            index[header[i]] = i;
        }

        // Model columns in header order (each -> its model name minus the suffix).
        var modelColumns = new List<(int Index, string Model)>();
        for (var i = 0; i < header.Length; i++)
        {
            if (ModelColumnPattern().IsMatch(header[i]))
            {
                modelColumns.Add((i, header[i][..^ModelSuffix.Length]));
            }
        }

        var partitions = new Dictionary<(string Date, string ClientType), FactPartition>();
        var rowsKept = 0;

        foreach (var row in rows)
        {
            var email = Field(row, index, ColUserEmail);

            // Fail-closed: a user absent from the Target List is never emitted.
            if (!targetEmails.Contains(email))
            {
                continue;
            }

            rowsKept++;

            var date = Field(row, index, ColDate);
            var clientType = Field(row, index, ColClientType);
            var userId = Field(row, index, ColUserId);

            var partition = GetOrCreatePartition(partitions, date, clientType);

            partition.UsageDaily.Add(new UsageDailyRecord
            {
                UserId = userId,
                UserEmail = email,
                ChatConversations = ParseLong(Field(row, index, ColChatConversations)),
                CreditsUsed = ParseDouble(Field(row, index, ColCreditsUsed)),
                OverageCap = ParseDouble(Field(row, index, ColOverageCap)),
                OverageCreditsUsed = ParseDouble(Field(row, index, ColOverageCreditsUsed)),
                OverageEnabled = ParseBool(Field(row, index, ColOverageEnabled)),
                SubscriptionTier = Field(row, index, ColSubscriptionTier),
                TotalMessages = ParseLong(Field(row, index, ColTotalMessages)),
                NewUser = ParseBool(Field(row, index, ColNewUser)),
                ProfileId = Field(row, index, ColProfileId),
            });

            // Unpivot the dynamic model columns; drop zero (and defensively negative) counts.
            foreach (var (col, model) in modelColumns)
            {
                if (col >= row.Length)
                {
                    continue;
                }

                var messages = ParseLong(row[col]);
                if (messages <= 0)
                {
                    continue;
                }

                partition.ModelMessages.Add(new ModelMessageRecord
                {
                    UserId = userId,
                    UserEmail = email,
                    Model = model,
                    Messages = messages,
                });
            }
        }

        return new TransformResult
        {
            RowsRead = rows.Count,
            RowsKept = rowsKept,
            Partitions = [.. partitions.Values],
        };
    }

    // Deterministic output key: source CSV basename + .parquet under the partition
    // path, so a re-fire (or backfill overlap) overwrites the same object.
    public static string OutputKey(string factPrefix, string date, string clientType, string sourceKey) =>
        $"{factPrefix}/date={date}/client_type={clientType}/{BaseName(sourceKey)}.parquet";

    public static string BaseName(string sourceKey)
    {
        var name = sourceKey;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        return name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static FactPartition GetOrCreatePartition(
        Dictionary<(string, string), FactPartition> partitions, string date, string clientType)
    {
        var key = (date, clientType);
        if (!partitions.TryGetValue(key, out var partition))
        {
            partition = new FactPartition { Date = date, ClientType = clientType };
            partitions[key] = partition;
        }

        return partition;
    }

    private static string Field(string[] row, Dictionary<string, int> index, string column) =>
        index.TryGetValue(column, out var i) && i < row.Length ? row[i] : "";

    private static long ParseLong(string value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;

    private static bool ParseBool(string value) =>
        bool.TryParse(value, out var v) && v;
}
