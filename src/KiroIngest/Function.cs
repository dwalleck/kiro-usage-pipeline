using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;

namespace KiroIngest;

// Polymorphic handler: dispatches on event shape.
//   S3 ObjectCreated notification → live path (process one key)
//   {"mode":"backfill", "from":?, "to":?}  → backfill path (list-and-process loop)
// Both converge on ProcessCsv so the transform + Target-List filter are identical.
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

    // Stream-based entry point: the raw Lambda event body. We peek at the JSON to
    // decide between live (S3 event) and backfill paths.
    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        // Buffer the stream so we can peek at the JSON shape, then deserialize.
        using var ms = new MemoryStream();
        await input.CopyToAsync(ms);
        ms.Position = 0;

        using var doc = await JsonDocument.ParseAsync(ms);

        if (doc.RootElement.TryGetProperty("mode", out var modeProp) &&
            modeProp.GetString() == "backfill")
        {
            ms.Position = 0;
            var backfill = await JsonSerializer.DeserializeAsync<BackfillRequest>(ms);
            await _ingest.ProcessBackfillAsync(backfill!.From, backfill.To, context);
        }
        else
        {
            ms.Position = 0;
            var s3Event = await JsonSerializer.DeserializeAsync<S3Event>(ms);
            await HandleS3EventAsync(s3Event!, context);
        }
    }

    private async Task HandleS3EventAsync(S3Event evnt, ILambdaContext context)
    {
        if (evnt.Records is null)
        {
            return;
        }

        foreach (var record in evnt.Records)
        {
            var bucket = record.S3.Bucket.Name;
            // S3 event keys are URL-encoded (e.g. '+' for spaces).
            var key = System.Net.WebUtility.UrlDecode(record.S3.Object.Key);
            await _ingest.ProcessCsv(bucket, key, context);
        }
    }
}
