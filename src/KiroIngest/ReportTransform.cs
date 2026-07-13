using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroIngest;

// Pure transform: User Activity Report CSV text + Target List -> the two facts,
// grouped by (date, client_type) partition. No AWS or I/O dependencies.
public static partial class ReportTransform
{
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

    private static readonly string[] RequiredColumns =
    [
        ColDate,
        ColUserId,
        ColClientType,
        ColChatConversations,
        ColCreditsUsed,
        ColOverageCap,
        ColOverageCreditsUsed,
        ColOverageEnabled,
        ColProfileId,
        ColSubscriptionTier,
        ColTotalMessages,
        ColNewUser,
        ColUserEmail,
    ];

    private static readonly HashSet<string> SupportedClientTypes =
        new(["KIRO_CLI", "KIRO_IDE", "PLUGIN"], StringComparer.Ordinal);

    [GeneratedRegex("^(?<model>[a-z0-9._]+)_messages$")]
    private static partial Regex ModelColumnPattern();

    public static TransformResult Transform(string csvText, ISet<string> targetList)
    {
        var (header, rows) = Csv.Parse(csvText);
        if (header.Length == 0)
        {
            throw new InvalidDataException("User Activity Report is empty");
        }

        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var modelColumns = new List<(int Index, string Model)>();

        for (var i = 0; i < header.Length; i++)
        {
            var column = header[i];
            if (string.IsNullOrWhiteSpace(column) || !index.TryAdd(column, i))
            {
                throw new InvalidDataException($"User Activity Report contains an empty or duplicate column: '{column}'");
            }

            var match = ModelColumnPattern().Match(column);
            if (match.Success)
            {
                modelColumns.Add((i, match.Groups["model"].Value));
            }
        }

        foreach (var requiredColumn in RequiredColumns)
        {
            if (!index.ContainsKey(requiredColumn))
            {
                throw new InvalidDataException($"User Activity Report is missing required column '{requiredColumn}'");
            }
        }

        var partitions = new Dictionary<(string Date, string ClientType), FactPartition>();
        var usageGrains = new HashSet<(string Date, string UserId, string ClientType)>();
        var rowsKept = 0;

        foreach (var row in rows)
        {
            if (row.Length != header.Length)
            {
                throw new InvalidDataException($"User Activity Report row has {row.Length} columns; expected {header.Length}");
            }

            var email = RequiredString(Field(row, index, ColUserEmail), ColUserEmail);
            if (!targetList.Contains(email))
            {
                continue;
            }

            var date = ParseDate(Field(row, index, ColDate));
            var clientType = ParseClientType(Field(row, index, ColClientType));
            var userId = RequiredString(Field(row, index, ColUserId), ColUserId);
            var grain = (date, userId, clientType);
            if (!usageGrains.Add(grain))
            {
                throw new InvalidDataException(
                    $"User Activity Report contains duplicate Daily Usage Fact grain ({date}, {userId}, {clientType})");
            }

            rowsKept++;
            var partition = GetOrCreatePartition(partitions, date, clientType);
            partition.UsageDaily.Add(new UsageDailyRecord
            {
                UserId = userId,
                UserEmail = email,
                ChatConversations = ParseNonNegativeLong(Field(row, index, ColChatConversations), ColChatConversations),
                CreditsUsed = ParseNonNegativeFiniteDouble(Field(row, index, ColCreditsUsed), ColCreditsUsed),
                OverageCap = ParseNonNegativeFiniteDouble(Field(row, index, ColOverageCap), ColOverageCap),
                OverageCreditsUsed = ParseNonNegativeFiniteDouble(Field(row, index, ColOverageCreditsUsed), ColOverageCreditsUsed),
                OverageEnabled = ParseBool(Field(row, index, ColOverageEnabled), ColOverageEnabled),
                SubscriptionTier = RequiredString(Field(row, index, ColSubscriptionTier), ColSubscriptionTier),
                TotalMessages = ParseNonNegativeLong(Field(row, index, ColTotalMessages), ColTotalMessages),
                NewUser = ParseBool(Field(row, index, ColNewUser), ColNewUser),
                ProfileId = RequiredString(Field(row, index, ColProfileId), ColProfileId),
            });

            foreach (var (columnIndex, model) in modelColumns)
            {
                var messages = ParseNonNegativeLong(row[columnIndex], header[columnIndex]);
                if (messages == 0)
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

    public static string OutputKey(
        string factPrefix,
        string date,
        string clientType,
        string sourceBucket,
        string sourceKey) =>
        $"{factPrefix}/date={date}/client_type={clientType}/{OutputFileName(sourceBucket, sourceKey)}";

    public static string OutputFileName(string sourceBucket, string sourceKey) =>
        $"{BaseName(sourceKey)}-{SourceId(sourceBucket, sourceKey)[..16]}.parquet";

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

    public static string SourceId(string sourceBucket, string sourceKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{sourceBucket}\n{sourceKey}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static FactPartition GetOrCreatePartition(
        Dictionary<(string, string), FactPartition> partitions,
        string date,
        string clientType)
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
        row[index[column]];

    private static string RequiredString(string value, string column)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException($"User Activity Report column '{column}' must not be empty");
        }

        return value;
    }

    private static string ParseDate(string value)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            throw new InvalidDataException($"User Activity Report Date '{value}' is not ISO yyyy-MM-dd");
        }

        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string ParseClientType(string value)
    {
        if (!SupportedClientTypes.Contains(value))
        {
            throw new InvalidDataException($"User Activity Report Client_Type '{value}' is not supported");
        }

        return value;
    }

    private static long ParseNonNegativeLong(string value, string column)
    {
        if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new InvalidDataException($"User Activity Report column '{column}' must be a non-negative integer");
        }

        return parsed;
    }

    private static double ParseNonNegativeFiniteDouble(string value, string column)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed < 0)
        {
            throw new InvalidDataException($"User Activity Report column '{column}' must be a non-negative finite number");
        }

        return parsed;
    }

    private static bool ParseBool(string value, string column)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new InvalidDataException($"User Activity Report column '{column}' must be true or false");
        }

        return parsed;
    }
}
