using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;

namespace KiroIngest;

// Polymorphic handler: dispatches on event shape.
//   S3 ObjectCreated notification → live path (process one key)
//   {"mode":"backfill", "from":?, "to":?}  → backfill path (list-and-process loop)
// Both converge on ProcessCsv so the transform + Target-List filter are identical.
// Single-pass JSON parse: JsonDocument reads the stream once, then typed
// deserialization reuses the in-memory DOM — no buffering, no rewinding.
public sealed class Function
{
    private readonly IIngestService _ingest;

    public Function()
        : this(IngestServiceFactory.FromEnvironment())
    {
    }

    public Function(IIngestService ingest)
    {
        _ingest = ingest;
    }

    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        using var doc = await JsonDocument.ParseAsync(input);

        if (doc.RootElement.TryGetProperty("mode", out var modeProp) &&
            modeProp.GetString() == "backfill")
        {
            var backfill = JsonSerializer.Deserialize<BackfillRequest>(doc.RootElement)
                ?? throw new InvalidOperationException("Failed to parse backfill payload from JSON");
            await _ingest.ProcessBackfillAsync(backfill.From, backfill.To, context);
        }
        else
        {
            var s3Event = JsonSerializer.Deserialize<S3Event>(doc.RootElement)
                ?? throw new InvalidOperationException("Failed to parse S3 event from JSON");
            await HandleS3EventAsync(s3Event, context);
        }
    }

    private async Task HandleS3EventAsync(S3Event evnt, ILambdaContext context)
    {
        foreach (var record in evnt.Records ?? [])
        {
            var bucket = record.S3.Bucket.Name;
            // S3 event keys are URL-encoded (e.g. '+' for spaces).
            var key = System.Net.WebUtility.UrlDecode(record.S3.Object.Key);
            await _ingest.ProcessCsv(bucket, key, context);
        }
    }
}
