using System.Globalization;
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
        var csv = await ReadObjectTextAsync(bucket, key);
        var targets = await GetTargetEmailsAsync();
        var partitions = ReportTransform.Transform(csv, targets);

        var usageWritten = 0;
        var modelWritten = 0;

        foreach (var partition in partitions)
        {
            if (partition.UsageDaily.Count > 0)
            {
                await WriteParquetAsync(
                    ReportTransform.OutputKey(UsageDailyPrefix, partition.Date, partition.ClientType, key),
                    await ParquetSerialization.SerializeAsync(partition.UsageDaily));
                usageWritten += partition.UsageDaily.Count;
            }

            if (partition.ModelMessages.Count > 0)
            {
                await WriteParquetAsync(
                    ReportTransform.OutputKey(ModelMessagesPrefix, partition.Date, partition.ClientType, key),
                    await ParquetSerialization.SerializeAsync(partition.ModelMessages));
                modelWritten += partition.ModelMessages.Count;
            }
        }

        context?.Logger.LogInformation(
            $"Ingested s3://{bucket}/{key}: partitions={partitions.Count}, " +
            $"usage_daily_rows={usageWritten}, model_messages_rows={modelWritten}");
    }

    // Backfill: enumerate all .csv objects under the raw prefix, optionally bounded by
    // from/to ISO dates, and process each sequentially. The same ProcessCsv core
    // (transform + Target-List filter + Parquet write) is reused identically.
    public async Task ProcessBackfillAsync(string? fromDate, string? toDate, ILambdaContext? context = null)
    {
        DateOnly? from = fromDate is not null ? ParseDateOnly(fromDate) : null;
        DateOnly? to = toDate is not null ? ParseDateOnly(toDate) : null;

        context?.Logger.LogInformation(
            $"Backfill: listing objects under s3://{_rawBucket}/{_rawPrefix} " +
            $"(from={fromDate ?? "unbounded"}, to={toDate ?? "unbounded"})");

        var matchingKeys = new List<string>();
        string? continuationToken = null;

        do
        {
            var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _rawBucket,
                Prefix = _rawPrefix,
                ContinuationToken = continuationToken,
            });

            foreach (var obj in response.S3Objects)
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

                    if (from is not null && keyDate < from)
                    {
                        continue;
                    }

                    if (to is not null && keyDate > to)
                    {
                        continue;
                    }
                }

                matchingKeys.Add(obj.Key);
            }

            continuationToken = response.IsTruncated == true ? response.NextContinuationToken : null;
        }
        while (continuationToken is not null);

        context?.Logger.LogInformation(
            $"Backfill: found {matchingKeys.Count} CSV objects to process");

        for (var i = 0; i < matchingKeys.Count; i++)
        {
            context?.Logger.LogInformation(
                $"Backfill: [{i + 1}/{matchingKeys.Count}] s3://{_rawBucket}/{matchingKeys[i]}");
            await ProcessCsv(_rawBucket, matchingKeys[i], context);
        }

        context?.Logger.LogInformation(
            $"Backfill: complete — {matchingKeys.Count} objects processed");
    }

    // Extract the report date from the Hive-style path segments:
    // .../user_report/{region}/{yyyy}/{mm}/{dd}/00/{filename}.csv
    private static DateOnly? ExtractDateFromKey(string key)
    {
        var parts = key.Split('/');
        // Walk backwards from the end:
        //   ^1 = filename.csv, ^2 = "00", ^3 = dd, ^4 = mm, ^5 = yyyy
        if (parts.Length < 6)
        {
            return null;
        }

        var dd = parts[^3];   // e.g. "10"
        var mm = parts[^4];   // e.g. "07"
        var yyyy = parts[^5]; // e.g. "2026"

        if (int.TryParse(yyyy, out var year) &&
            int.TryParse(mm, out var month) &&
            int.TryParse(dd, out var day) &&
            year >= 2000 && year <= 2100 &&
            month >= 1 && month <= 12 &&
            day >= 1 && day <= 31)
        {
            return new DateOnly(year, month, day);
        }

        return null;
    }

    private static DateOnly ParseDateOnly(string value) =>
        DateOnly.Parse(value, CultureInfo.InvariantCulture);

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

    private async Task WriteParquetAsync(string key, byte[] parquet)
    {
        using var body = new MemoryStream(parquet);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _analyticsBucket,
            Key = key,
            InputStream = body,
            ContentType = "application/octet-stream",
        });
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
