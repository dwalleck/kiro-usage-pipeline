using System.Text.Json.Serialization;

namespace KiroIngest;

// Payload for a manual backfill invoke: {"mode":"backfill", "from":"2026-06-20", "to":"2026-07-10"}.
// from and to are optional ISO dates; both default to unbounded.
public sealed class BackfillRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }
}
