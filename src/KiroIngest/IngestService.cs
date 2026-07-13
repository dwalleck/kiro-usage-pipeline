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
public sealed class IngestService
{
    private const string UsageDailyPrefix = "usage_daily";
    private const string ModelMessagesPrefix = "model_messages";

    private readonly IAmazonS3 _s3;
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly string _analyticsBucket;
    private readonly string _targetListParameter;

    // Cached across warm invocations; the Target List rarely changes and is editable
    // without a redeploy, so a cold start picks up edits.
    private ISet<string>? _targetEmails;

    public IngestService(
        IAmazonS3 s3,
        IAmazonSimpleSystemsManagement ssm,
        string analyticsBucket,
        string targetListParameter)
    {
        _s3 = s3;
        _ssm = ssm;
        _analyticsBucket = analyticsBucket;
        _targetListParameter = targetListParameter;
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
