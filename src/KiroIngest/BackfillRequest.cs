using System.Text.Json.Serialization;

namespace KiroIngest;

// Payload for a manual backfill invoke: {"mode":"backfill", "from":"2026-06-20", "to":"2026-07-10"}.
// from and to are optional ISO dates, parsed by System.Text.Json natively; both default to unbounded.
public sealed class BackfillRequest
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("from")]
    public DateOnly? From { get; set; }

    [JsonPropertyName("to")]
    public DateOnly? To { get; set; }
}
