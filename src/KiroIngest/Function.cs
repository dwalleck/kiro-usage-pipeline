using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;

namespace KiroIngest;

// Polymorphic handler: dispatches on event shape.
//   S3 ObjectCreated notification → live path (process each key)
//   {"mode":"backfill", "from":?, "to":?} → backfill path
// Both converge on ProcessCsvAsync so the transform + Target List filter are identical.
public sealed class Function
{
    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IIngestService _ingest;

    public Function()
        : this(IngestServiceFactory.FromEnvironment())
    {
    }

    public Function(IIngestService ingest)
    {
        _ingest = ingest ?? throw new ArgumentNullException(nameof(ingest));
    }

    public async Task HandleAsync(Stream input, ILambdaContext context)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(input);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException("Lambda payload must be a JSON object");
            }

            if (doc.RootElement.TryGetProperty("mode", out var modeProperty))
            {
                await HandleModeRequestAsync(doc.RootElement, modeProperty, context);
                return;
            }

            if (!doc.RootElement.TryGetProperty("Records", out var recordsProperty) ||
                recordsProperty.ValueKind != JsonValueKind.Array ||
                recordsProperty.GetArrayLength() == 0)
            {
                throw new InvalidOperationException("Unsupported Lambda payload: expected a non-empty S3 Records array or mode=backfill");
            }

            var s3Event = JsonSerializer.Deserialize<S3Event>(doc.RootElement, EventJsonOptions)
                ?? throw new InvalidOperationException("Failed to parse S3 event from JSON");
            await HandleS3EventAsync(s3Event, context);
        }
        catch (Exception ex)
        {
            context.Logger.LogError(JsonSerializer.Serialize(new
            {
                @event = "invocation_error",
                error = ex.Message,
                details = ex.ToString(),
                type = ex.GetType().Name,
            }));
            throw;
        }
    }

    private async Task HandleModeRequestAsync(
        JsonElement root,
        JsonElement modeProperty,
        ILambdaContext context)
    {
        if (modeProperty.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Unsupported Lambda mode; expected '{BackfillRequest.ModeValue}'");
        }

        var backfill = JsonSerializer.Deserialize<BackfillRequest>(root, EventJsonOptions)
            ?? throw new InvalidOperationException("Failed to parse backfill payload from JSON");
        backfill.Validate();

        await _ingest.ProcessBackfillAsync(
            backfill.From,
            backfill.To,
            context,
            backfill.ContinuationToken);
    }

    private async Task HandleS3EventAsync(S3Event s3Event, ILambdaContext context)
    {
        var failures = new List<Exception>();

        foreach (var record in s3Event.Records ?? [])
        {
            try
            {
                var s3 = record.S3;
                var objectEntity = s3?.Object;
                var bucket = s3?.Bucket?.Name;
                if (s3 is null ||
                    objectEntity is null ||
                    string.IsNullOrWhiteSpace(bucket) ||
                    string.IsNullOrWhiteSpace(objectEntity.Key))
                {
                    throw new InvalidOperationException("S3 event record is missing bucket or object key");
                }

                var source = new IngestSource(
                    bucket,
                    objectEntity.KeyDecoded,
                    objectEntity.VersionId,
                    objectEntity.Sequencer);
                await _ingest.ProcessCsvAsync(source, context);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("One or more S3 event records failed", failures);
        }
    }
}
