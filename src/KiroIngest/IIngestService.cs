using Amazon.Lambda.Core;

namespace KiroIngest;

// Test seam: the service methods consumed by the Lambda handler. Production
// code uses IngestService through IngestServiceFactory.
public interface IIngestService
{
    Task ProcessCsv(IngestSource source, ILambdaContext? context = null);

    // Backfill: list all .csv objects under the raw prefix and process each one
    // sequentially. from/to are optional DateOnly bounds (default unbounded).
    Task ProcessBackfillAsync(
        DateOnly? from,
        DateOnly? to,
        ILambdaContext? context = null,
        string? continuationToken = null);
}
