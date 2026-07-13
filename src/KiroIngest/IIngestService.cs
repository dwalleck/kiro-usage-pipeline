using Amazon.Lambda.Core;

namespace KiroIngest;

// Test seam: the service methods consumed by the Lambda handler. Extracted
// so Function can depend on the interface (mockable in unit tests) while production
// code uses IngestService through IngestServiceFactory.
public interface IIngestService
{
    Task ProcessCsv(string bucket, string key, ILambdaContext? context = null);

    // Backfill: list all .csv objects under the raw prefix and process each one
    // sequentially. from/to are optional ISO date bounds (default unbounded).
    Task ProcessBackfillAsync(string? fromDate, string? toDate, ILambdaContext? context = null);
}
