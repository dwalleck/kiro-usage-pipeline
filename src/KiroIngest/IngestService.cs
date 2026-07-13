using System.Globalization;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace KiroIngest;

// Orchestrates the per-object pipeline: read a User Activity Report CSV, filter to
// the Target List, Unpivot, and write the two facts as Parquet under deterministic,
// partitioned keys. Both the live S3 path and (ticket 11) the backfill loop call
// ProcessCsv, so the transform and Target-List filter apply identically.
public sealed class IngestService : IIngestService
{
    private const string UsageDailyPrefix = "usage_daily";
    private const string ModelMessagesPrefix = "model_messages";

    private readonly IAmazonS3 _s3;
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly string _analyticsBucket;
    private readonly string _targetListParameter;
    private readonly string _rawBucket;
    private readonly string _rawPrefix;

    // Cached across warm invocations; the Target List rarely changes and is editable
    // without a redeploy, so a cold start picks up edits.
    private ISet<string>? _targetEmails;

    public IngestService(
        IAmazonS3 s3,
        IAmazonSimpleSystemsManagement ssm,
        string analyticsBucket,
        string targetListParameter,
        string rawBucket,
        string rawPrefix)
    {
        _s3 = s3;
        _ssm = ssm;
        _analyticsBucket = analyticsBucket;
        _targetListParameter = targetListParameter;
        _rawBucket = rawBucket;
        _rawPrefix = rawPrefix;
    }

    public async Task ProcessCsv(string bucket, string key, ILambdaContext? context = null)
    {
        try
        {
            var csv = await ReadObjectTextAsync(bucket, key);
            var targets = await GetTargetEmailsAsync();
            var result = ReportTransform.Transform(csv, targets);

            var usageWritten = 0;
            var modelWritten = 0;

            foreach (var partition in result.Partitions)
            {
                if (partition.UsageDaily.Count > 0)
                {
                    using var parquet = await ParquetSerialization.SerializeAsync(partition.UsageDaily);
                    await WriteParquetAsync(
                        ReportTransform.OutputKey(UsageDailyPrefix, partition.Date, partition.ClientType, key),
                        parquet);
                    usageWritten += partition.UsageDaily.Count;
                }

                if (partition.ModelMessages.Count > 0)
                {
                    using var parquet = await ParquetSerialization.SerializeAsync(partition.ModelMessages);
                    await WriteParquetAsync(
                        ReportTransform.OutputKey(ModelMessagesPrefix, partition.Date, partition.ClientType, key),
                        parquet);
                    modelWritten += partition.ModelMessages.Count;
                }
            }

            context?.Logger.LogInformation(JsonSerializer.Serialize(new
            {
                @event = "ingest_complete",
                source = new S3Source { Bucket = bucket, Key = key },
                rows_read = result.RowsRead,
                rows_kept = result.RowsKept,
                usage_daily_rows = usageWritten,
                model_messages_rows = modelWritten,
                partitions = result.Partitions.Count,
            }));
        }
        catch (Exception ex)
        {
            LogError(context, bucket, key, ex);
            throw;
        }
    }

    // Backfill: enumerate all .csv objects under the raw prefix, optionally bounded by
    // from/to DateOnly bounds, and process each sequentially per page (no full-list
    // buffering). The same ProcessCsv core (transform + Target-List filter + Parquet
    // write) is reused identically.
    public async Task ProcessBackfillAsync(DateOnly? from, DateOnly? to, ILambdaContext? context = null)
    {
        context?.Logger.LogInformation(
            $"Backfill: listing objects under s3://{_rawBucket}/{_rawPrefix} " +
            $"(from={from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unbounded"}, to={to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "unbounded"})");

        var processed = 0;
        string? continuationToken = null;

        do
        {
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _rawBucket,
                Prefix = _rawPrefix,
                ContinuationToken = continuationToken,
            });

            foreach (var obj in response.S3Objects ?? [])
            {
                if (!obj.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (from is not null || to is not null)
                {
                    var keyDate = ExtractDateFromKey(obj.Key);
                    if (keyDate is null)
                    {
                        continue;
                    }

                    if (keyDate < from)
                    {
                        continue;
                    }

                    if (keyDate > to)
                    {
                        continue;
                    }
                }

                processed++;
                context?.Logger.LogInformation(
                    $"Backfill: [{processed}] s3://{_rawBucket}/{obj.Key}");
                await ProcessCsv(_rawBucket, obj.Key, context);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        context?.Logger.LogInformation(
            $"Backfill: complete — {processed} objects processed");
    }

    // Structured error log emitted before the exception propagates, so the Lambda
    // invocation fails and (after retry exhaustion) the event lands on the DLQ.
    private static void LogError(ILambdaContext? context, string bucket, string key, Exception ex)
    {
        context?.Logger.LogError(JsonSerializer.Serialize(new
        {
            @event = "ingest_error",
            source = new S3Source { Bucket = bucket, Key = key },
            error = ex.Message,
            type = ex.GetType().Name,
        }));
    }

    // Extract the report date from the Hive-style path segments:
    // .../user_report/{region}/{yyyy}/{mm}/{dd}/00/{filename}.csv
    // Uses TryParseExact for calendar validation (e.g. Feb 30 → null).
    private static DateOnly? ExtractDateFromKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length < 6)
        {
            return null;
        }

        var dateStr = $"{parts[^5]}-{parts[^4]}-{parts[^3]}";
        return DateOnly.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;
    }

    private async Task<string> ReadObjectTextAsync(string bucket, string key)
    {
        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
        });
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    private async Task WriteParquetAsync(string key, Stream parquet)
    {
        using (parquet)
        {
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _analyticsBucket,
                Key = key,
                InputStream = parquet,
                ContentType = "application/octet-stream",
            });
        }
    }

    private async Task<ISet<string>> GetTargetEmailsAsync()
    {
        if (_targetEmails is not null)
        {
            return _targetEmails;
        }

        var response = await _ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = _targetListParameter,
        });

        // StringList parameters are returned as a single comma-separated value.
        var emails = response.Parameter.Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        _targetEmails = new HashSet<string>(emails, StringComparer.Ordinal);
        return _targetEmails;
    }
}
