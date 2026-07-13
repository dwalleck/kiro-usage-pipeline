using System.Text.Json.Serialization;

namespace KiroIngest;

// Parquet DTOs. Property names are PascalCase (C# convention) and mapped to
// snake_case Parquet column names via [JsonPropertyName] to match Glue
// table columns exactly (ticket 09). `date` and `client_type` are path
// partitions and are deliberately NOT here.
public sealed class UsageDailyRecord
{
    [JsonPropertyName("user_id")]              public string UserId { get; set; } = "";
    [JsonPropertyName("user_email")]           public string UserEmail { get; set; } = "";
    [JsonPropertyName("chat_conversations")]   public long ChatConversations { get; set; }
    [JsonPropertyName("credits_used")]         public double CreditsUsed { get; set; }
    [JsonPropertyName("overage_cap")]          public double OverageCap { get; set; }
    [JsonPropertyName("overage_credits_used")] public double OverageCreditsUsed { get; set; }
    [JsonPropertyName("overage_enabled")]      public bool OverageEnabled { get; set; }
    [JsonPropertyName("subscription_tier")]    public string SubscriptionTier { get; set; } = "";
    [JsonPropertyName("total_messages")]       public long TotalMessages { get; set; }
    [JsonPropertyName("new_user")]             public bool NewUser { get; set; }
    [JsonPropertyName("profile_id")]           public string ProfileId { get; set; } = "";
}

public sealed class ModelMessageRecord
{
    [JsonPropertyName("user_id")]  public string UserId { get; set; } = "";
    [JsonPropertyName("user_email")] public string UserEmail { get; set; } = "";
    [JsonPropertyName("model")]    public string Model { get; set; } = "";
    [JsonPropertyName("messages")] public long Messages { get; set; }
}

// Lightweight DTO for the structured log source field, serialized as a nested
// object in the ingest_complete and ingest_error log lines.
public sealed class S3Source
{
    public string Bucket { get; set; } = "";
    public string Key { get; set; } = "";
}

// Result of the CSV-to-facts transform, carrying row-count metadata so the
// ingest service can emit structured observability logs.
public sealed class TransformResult
{
    public required int RowsRead { get; init; }
    public required int RowsKept { get; init; }
    public required IReadOnlyList<FactPartition> Partitions { get; init; }
}

// The two facts for one (date, client_type) partition, ready to write as Parquet.
public sealed class FactPartition
{
    public required string Date { get; init; }
    public required string ClientType { get; init; }
    public List<UsageDailyRecord> UsageDaily { get; } = [];
    public List<ModelMessageRecord> ModelMessages { get; } = [];
}
