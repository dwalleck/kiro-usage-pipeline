using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;

namespace KiroIngest;

// Live path: process each object in an S3 ObjectCreated notification. The event
// filter (prefix user_report/, suffix .csv) is configured on the bucket in CDK, so
// only User Activity Report CSVs reach here. Ticket 11 adds the backfill branch.
public sealed class Function
{
    private readonly IngestService _ingest;

    public Function()
        : this(IngestServiceFactory.FromEnvironment())
    {
    }

    public Function(IngestService ingest)
    {
        _ingest = ingest;
    }

    public async Task HandleAsync(S3Event evnt, ILambdaContext context)
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
