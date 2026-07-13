using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace KiroIngest;

public interface IBackfillContinuationScheduler
{
    Task ScheduleAsync(DateOnly? from, DateOnly? to, string continuationToken);
}

// Starts the next bounded page as a separate asynchronous invocation so each
// page receives Lambda retries and DLQ handling independently.
public sealed class LambdaBackfillContinuationScheduler(
    IAmazonLambda lambda,
    string functionName) : IBackfillContinuationScheduler
{
    public async Task ScheduleAsync(DateOnly? from, DateOnly? to, string continuationToken)
    {
        var payload = JsonSerializer.Serialize(new BackfillRequest
        {
            Mode = BackfillRequest.ModeValue,
            From = from,
            To = to,
            ContinuationToken = continuationToken,
        });

        try
        {
            await lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = functionName,
                InvocationType = InvocationType.Event,
                Payload = payload,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to schedule backfill continuation (from={from}, to={to})",
                ex);
        }
    }
}
