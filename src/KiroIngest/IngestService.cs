using System.Globalization;
using System.Net;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace KiroIngest;

// Orchestrates the per-object pipeline: read a User Activity Report CSV, filter to
// the Target List, Unpivot, and reconcile the two Parquet facts. Both live S3
// events and backfill use this core.
public sealed class IngestService : IIngestService
{
    private const string UsageDailyPrefix = "usage_daily";
    private const string ModelMessagesPrefix = "model_messages";
    private const string StatePrefix = "ingest-state";

    private readonly IAmazonS3 _s3;
    private readonly IAmazonSimpleSystemsManagement _ssm;
    private readonly IBackfillContinuationScheduler _continuationScheduler;
    private readonly string _analyticsBucket;
    private readonly string _targetListParameter;
    private readonly string _rawBucket;
    private readonly string _rawPrefix;

    public IngestService(
        IAmazonS3 s3,
        IAmazonSimpleSystemsManagement ssm,
        IBackfillContinuationScheduler continuationScheduler,
        string analyticsBucket,
        string targetListParameter,
        string rawBucket,
        string rawPrefix)
    {
        _s3 = s3;
        _ssm = ssm;
        _continuationScheduler = continuationScheduler;
        _analyticsBucket = analyticsBucket;
        _targetListParameter = targetListParameter;
        _rawBucket = rawBucket;
        _rawPrefix = rawPrefix;
    }

    public Task ProcessCsvAsync(string bucket, string key, ILambdaContext? context = null) =>
        ProcessCsvAsync(new IngestSource(bucket, key), context);

    public async Task ProcessCsvAsync(IngestSource source, ILambdaContext? context = null)
    {
        var preparedOutputs = new List<PreparedOutput>();
        var attemptedOutputKeys = new List<string>();
        IReadOnlyCollection<string> existingOutputKeys = [];
        var publicationComplete = false;

        try
        {
            if (await IsStaleEventAsync(source))
            {
                context?.Logger.LogInformation(JsonSerializer.Serialize(new
                {
                    @event = "ingest_skipped_stale",
                    source,
                }));
                return;
            }

            var csv = await ReadObjectTextAsync(source);
            var targetList = await GetTargetListAsync();
            var result = ReportTransform.Transform(csv, targetList);
            WarnOnKeyDateMismatch(source, result, context);

            preparedOutputs.AddRange(await PrepareOutputsAsync(source, result));
            existingOutputKeys = await FindExistingOutputKeysAsync(source);
            var newOutputKeys = preparedOutputs.Select(output => output.Key).ToHashSet(StringComparer.Ordinal);

            foreach (var output in preparedOutputs)
            {
                // Track before awaiting: a timed-out request may have reached S3 even if
                // the client never received the response.
                attemptedOutputKeys.Add(output.Key);
                await WriteParquetAsync(output.Key, output.Stream);
            }

            foreach (var staleKey in existingOutputKeys.Where(key => !newOutputKeys.Contains(key)))
            {
                await DeleteOutputAsync(staleKey);
            }

            // The complete new generation is now visible. A later sequencer-state timeout
            // must not remove it: S3 may have committed the state even when the client did
            // not receive the response, and a retry would then correctly skip this event.
            publicationComplete = true;

            if (source.Sequencer is not null)
            {
                await SaveSequencerAsync(source);
            }

            context?.Logger.LogInformation(JsonSerializer.Serialize(new
            {
                @event = "ingest_complete",
                source,
                rows_read = result.RowsRead,
                rows_kept = result.RowsKept,
                usage_daily_rows = result.Partitions.Sum(partition => partition.UsageDaily.Count),
                model_messages_rows = result.Partitions.Sum(partition => partition.ModelMessages.Count),
                partitions = result.Partitions.Count,
            }));
        }
        catch (Exception ex)
        {
            if (attemptedOutputKeys.Count > 0 && !publicationComplete)
            {
                // Once publication begins, fail closed: remove both prior and attempted
                // outputs so Athena cannot observe a mixed generation. Before the first
                // PUT, or after a complete generation has been published, preserve data.
                var keysToRemove = existingOutputKeys
                    .Concat(attemptedOutputKeys)
                    .Distinct(StringComparer.Ordinal);
                await TryDeleteOutputsAsync(keysToRemove, source, context);
            }

            LogError(context, source, ex);
            throw;
        }
        finally
        {
            foreach (var output in preparedOutputs)
            {
                output.Stream.Dispose();
            }
        }
    }

    // Enumerate every canonical CSV under the raw prefix. Per-object failures are
    // isolated so a poison report cannot prevent later history from being tried;
    // the aggregate failure still fails the invocation for asynchronous retry/DLQ.
    public async Task ProcessBackfillAsync(
        DateOnly? from,
        DateOnly? to,
        ILambdaContext? context = null,
        string? continuationToken = null)
    {
        if (from is not null && to is not null && from > to)
        {
            throw new ArgumentException("Backfill 'from' date must be on or before 'to' date");
        }

        context?.Logger.LogInformation(JsonSerializer.Serialize(new
        {
            @event = "backfill_page_started",
            bucket = _rawBucket,
            prefix = _rawPrefix,
            from = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            to = to?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            continuation_token = continuationToken,
        }));

        var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _rawBucket,
            Prefix = _rawPrefix,
            ContinuationToken = continuationToken,
        });

        var nextContinuationToken = RequireContinuationToken(response, "backfill page");

        if (nextContinuationToken is not null)
        {
            // Schedule first so a poison object on this page cannot starve later pages.
            // Retries may schedule a duplicate continuation; deterministic output keys,
            // sequencing state, and reserved concurrency make that replay harmless.
            await _continuationScheduler.ScheduleAsync(from, to, nextContinuationToken);
        }

        var processed = 0;
        var failures = new List<Exception>();
        foreach (var obj in response.S3Objects ?? [])
        {
            if (!obj.Key.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var keyDate = ExtractDateFromKey(obj.Key);
            if (keyDate is null)
            {
                var error = new InvalidDataException($"Backfill key has no valid report date: {obj.Key}");
                failures.Add(error);
                context?.Logger.LogError(JsonSerializer.Serialize(new
                {
                    @event = "backfill_invalid_key",
                    bucket = _rawBucket,
                    key = obj.Key,
                    error = error.Message,
                }));
                continue;
            }

            if (keyDate < from || keyDate > to)
            {
                continue;
            }

            processed++;
            try
            {
                await ProcessCsvAsync(new IngestSource(_rawBucket, obj.Key), context);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        context?.Logger.LogInformation(JsonSerializer.Serialize(new
        {
            @event = "backfill_page_complete",
            objects_processed = processed,
            objects_failed = failures.Count,
            continuation_scheduled = nextContinuationToken is not null,
        }));

        if (failures.Count > 0)
        {
            throw new AggregateException($"Backfill failed for {failures.Count} object(s)", failures);
        }
    }

    private static async Task<List<PreparedOutput>> PrepareOutputsAsync(
        IngestSource source,
        TransformResult result)
    {
        var outputs = new List<PreparedOutput>();
        try
        {
            foreach (var partition in result.Partitions)
            {
                var usageStream = await ParquetSerialization.SerializeAsync(partition.UsageDaily);
                outputs.Add(new PreparedOutput(
                    ReportTransform.OutputKey(
                        UsageDailyPrefix,
                        partition.Date,
                        partition.ClientType,
                        source.Bucket,
                        source.Key),
                    usageStream));

                if (partition.ModelMessages.Count > 0)
                {
                    var modelStream = await ParquetSerialization.SerializeAsync(partition.ModelMessages);
                    outputs.Add(new PreparedOutput(
                        ReportTransform.OutputKey(
                            ModelMessagesPrefix,
                            partition.Date,
                            partition.ClientType,
                            source.Bucket,
                            source.Key),
                        modelStream));
                }
            }

            return outputs;
        }
        catch
        {
            foreach (var output in outputs)
            {
                output.Stream.Dispose();
            }

            throw;
        }
    }

    private async Task<IReadOnlyCollection<string>> FindExistingOutputKeysAsync(IngestSource source)
    {
        var currentFileName = ReportTransform.OutputFileName(source.Bucket, source.Key);
        var legacyFileName = $"{ReportTransform.BaseName(source.Key)}.parquet";
        var matches = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prefix in new[] { $"{UsageDailyPrefix}/", $"{ModelMessagesPrefix}/" })
        {
            string? continuationToken = null;
            do
            {
                var response = await _s3.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _analyticsBucket,
                    Prefix = prefix,
                    ContinuationToken = continuationToken,
                });

                foreach (var obj in response.S3Objects ?? [])
                {
                    var fileName = Path.GetFileName(obj.Key);
                    if (string.Equals(fileName, currentFileName, StringComparison.Ordinal) ||
                        string.Equals(fileName, legacyFileName, StringComparison.Ordinal))
                    {
                        matches.Add(obj.Key);
                    }
                }

                continuationToken = RequireContinuationToken(response, "analytics listing");
            }
            while (continuationToken is not null);
        }

        return matches;
    }

    private async Task<bool> IsStaleEventAsync(IngestSource source)
    {
        if (source.Sequencer is null)
        {
            return false;
        }

        var savedSequencer = await ReadSequencerAsync(source);
        return savedSequencer is not null && CompareSequencers(source.Sequencer, savedSequencer) <= 0;
    }

    private async Task<string?> ReadSequencerAsync(IngestSource source)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _analyticsBucket,
                Key = StateKey(source),
            });
            var state = await JsonSerializer.DeserializeAsync<SequencerState>(response.ResponseStream);
            return state?.Sequencer;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal))
        {
            return null;
        }
    }

    private Task<PutObjectResponse> SaveSequencerAsync(IngestSource source) =>
        _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _analyticsBucket,
            Key = StateKey(source),
            ContentBody = JsonSerializer.Serialize(new SequencerState { Sequencer = source.Sequencer! }),
            ContentType = "application/json",
        });

    private static int CompareSequencers(string left, string right)
    {
        if (!left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit))
        {
            throw new InvalidDataException("S3 event sequencer must be hexadecimal");
        }

        var normalizedLeft = left.TrimStart('0');
        var normalizedRight = right.TrimStart('0');
        normalizedLeft = normalizedLeft.Length == 0 ? "0" : normalizedLeft;
        normalizedRight = normalizedRight.Length == 0 ? "0" : normalizedRight;

        var lengthComparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
        return lengthComparison != 0
            ? lengthComparison
            : string.Compare(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    // The SDK's IsTruncated is a nullable bool; a truncated page must always carry a
    // token, otherwise pagination would silently drop the remaining objects.
    private static string? RequireContinuationToken(ListObjectsV2Response response, string listingDescription)
    {
        if (response.IsTruncated != true)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(response.NextContinuationToken))
        {
            throw new InvalidOperationException(
                $"S3 returned a truncated {listingDescription} without a continuation token");
        }

        return response.NextContinuationToken;
    }

    private static string StateKey(IngestSource source) =>
        $"{StatePrefix}/{ReportTransform.SourceId(source.Bucket, source.Key)}.json";

    // Body dates are authoritative for partitioning; the key-path date is only
    // addressing. A mismatch is a Kiro anomaly worth surfacing, but it must not fail
    // the object — live and backfill deliberately treat it identically (spec §6).
    private static void WarnOnKeyDateMismatch(
        IngestSource source,
        TransformResult result,
        ILambdaContext? context)
    {
        var keyDate = ExtractDateFromKey(source.Key);
        if (keyDate is null)
        {
            return;
        }

        var expected = keyDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mismatched = result.Partitions
            .Select(partition => partition.Date)
            .Where(date => !string.Equals(date, expected, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (mismatched.Length > 0)
        {
            context?.Logger.LogWarning(JsonSerializer.Serialize(new
            {
                @event = "ingest_date_mismatch",
                source,
                key_date = expected,
                report_dates = mismatched,
            }));
        }
    }

    private static DateOnly? ExtractDateFromKey(string key)
    {
        var parts = key.Split('/');
        if (parts.Length < 6)
        {
            return null;
        }

        var dateText = $"{parts[^5]}-{parts[^4]}-{parts[^3]}";
        return DateOnly.TryParseExact(
            dateText,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var date)
            ? date
            : null;
    }

    private async Task<string> ReadObjectTextAsync(IngestSource source)
    {
        using var response = await _s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = source.Bucket,
            Key = source.Key,
            VersionId = source.VersionId,
        });
        using var reader = new StreamReader(response.ResponseStream);
        return await reader.ReadToEndAsync();
    }

    private Task<PutObjectResponse> WriteParquetAsync(string key, Stream parquet) =>
        _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _analyticsBucket,
            Key = key,
            InputStream = parquet,
            ContentType = "application/octet-stream",
        });

    private Task<DeleteObjectResponse> DeleteOutputAsync(string key) =>
        _s3.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = _analyticsBucket,
            Key = key,
        });

    private async Task TryDeleteOutputsAsync(
        IEnumerable<string> keys,
        IngestSource source,
        ILambdaContext? context)
    {
        foreach (var key in keys)
        {
            try
            {
                await DeleteOutputAsync(key);
            }
            catch (Exception cleanupError)
            {
                context?.Logger.LogError(JsonSerializer.Serialize(new
                {
                    @event = "ingest_cleanup_error",
                    source,
                    output_key = key,
                    error = cleanupError.Message,
                    type = cleanupError.GetType().Name,
                }));
            }
        }
    }

    private async Task<ISet<string>> GetTargetListAsync()
    {
        var response = await _ssm.GetParameterAsync(new GetParameterRequest
        {
            Name = _targetListParameter,
        });

        var value = response.Parameter?.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("Target List parameter is empty");
        }

        var emails = value.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(emails, StringComparer.OrdinalIgnoreCase);
    }

    private static void LogError(ILambdaContext? context, IngestSource source, Exception ex)
    {
        context?.Logger.LogError(JsonSerializer.Serialize(new
        {
            @event = "ingest_error",
            source,
            error = ex.Message,
            type = ex.GetType().Name,
        }));
    }

    private sealed record PreparedOutput(string Key, MemoryStream Stream);

    private sealed class SequencerState
    {
        public string Sequencer { get; set; } = "";
    }
}
