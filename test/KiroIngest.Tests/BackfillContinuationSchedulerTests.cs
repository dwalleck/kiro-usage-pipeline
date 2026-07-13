using Amazon.Lambda;
using Amazon.Lambda.Model;
using KiroIngest;
using Moq;

namespace KiroIngest.Tests;

public class BackfillContinuationSchedulerTests
{
    [Test]
    public async Task ScheduleAsync_InvokeFails_ThrowsContextualException()
    {
        var lambda = new Mock<IAmazonLambda>();
        lambda.Setup(client => client.InvokeAsync(
                It.IsAny<InvokeRequest>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonLambdaException("throttled"));
        var scheduler = new LambdaBackfillContinuationScheduler(lambda.Object, "ingest-function");

        await Assert.That(() => scheduler.ScheduleAsync(null, null, "next-token"))
            .Throws<InvalidOperationException>()
            .WithMessage("Failed to schedule backfill continuation (from=, to=)");
    }

    [Test]
    public async Task ScheduleAsync_InvokesSameFunctionAsynchronouslyWithContinuation()
    {
        var lambda = new Mock<IAmazonLambda>();
        lambda.Setup(client => client.InvokeAsync(
                It.IsAny<InvokeRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new InvokeResponse());
        var scheduler = new LambdaBackfillContinuationScheduler(lambda.Object, "ingest-function");

        await scheduler.ScheduleAsync(
            new DateOnly(2026, 6, 20),
            new DateOnly(2026, 7, 10),
            "next-token");

        lambda.Verify(client => client.InvokeAsync(
            It.Is<InvokeRequest>(request =>
                request.FunctionName == "ingest-function" &&
                request.InvocationType == InvocationType.Event &&
                request.Payload.Contains("\"continuationToken\":\"next-token\"", StringComparison.Ordinal) &&
                request.Payload.Contains("\"from\":\"2026-06-20\"", StringComparison.Ordinal) &&
                request.Payload.Contains("\"to\":\"2026-07-10\"", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
