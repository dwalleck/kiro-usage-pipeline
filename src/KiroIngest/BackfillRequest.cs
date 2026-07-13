using System.Text.Json.Serialization;

namespace KiroIngest;

// Payload for a manual backfill invoke: {"mode":"backfill", "from":"2026-06-20", "to":"2026-07-10"}.
// from and to are optional ISO dates, parsed by System.Text.Json natively; both default to unbounded.
public sealed class BackfillRequest
{
    public const string ModeValue = "backfill";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("from")]
    public DateOnly? From { get; set; }

    [JsonPropertyName("to")]
    public DateOnly? To { get; set; }

    [JsonPropertyName("continuationToken")]
    public string? ContinuationToken { get; set; }

    public void Validate()
    {
        if (!string.Equals(Mode, ModeValue, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported Lambda mode; expected '{ModeValue}'");
        }

        if (From is not null && To is not null && From > To)
        {
            throw new ArgumentException("Backfill 'from' date must be on or before 'to' date");
        }

        if (ContinuationToken is not null && string.IsNullOrWhiteSpace(ContinuationToken))
        {
            throw new ArgumentException("Backfill continuation token must not be blank");
        }
    }
}
