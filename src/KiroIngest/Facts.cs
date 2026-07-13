namespace KiroIngest;

// Parquet DTOs. The property names ARE the Parquet column names, so they are
// lowercase snake_case to match the Glue table columns exactly (ticket 09).
// `date` and `client_type` are path partitions and are deliberately NOT here.
#pragma warning disable IDE1006 // intentional snake_case to match Parquet/Glue column names

public sealed class UsageDailyRecord
{
    public string user_id { get; set; } = "";
    public string user_email { get; set; } = "";
    public long chat_conversations { get; set; }
    public double credits_used { get; set; }
    public double overage_cap { get; set; }
    public double overage_credits_used { get; set; }
    public bool overage_enabled { get; set; }
    public string subscription_tier { get; set; } = "";
    public long total_messages { get; set; }
    public bool new_user { get; set; }
    public string profile_id { get; set; } = "";
}

public sealed class ModelMessageRecord
{
    public string user_id { get; set; } = "";
    public string user_email { get; set; } = "";
    public string model { get; set; } = "";
    public long messages { get; set; }
}

#pragma warning restore IDE1006

// The two facts for one (date, client_type) partition, ready to write as Parquet.
public sealed class FactPartition
{
    public required string Date { get; init; }
    public required string ClientType { get; init; }
    public List<UsageDailyRecord> UsageDaily { get; } = [];
    public List<ModelMessageRecord> ModelMessages { get; } = [];
}
