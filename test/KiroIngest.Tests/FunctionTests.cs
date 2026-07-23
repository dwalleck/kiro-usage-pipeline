using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using KiroIngest;
using Moq;

namespace KiroIngest.Tests;

public class FunctionTests
{
    private static Stream ToStream(string json) =>
        new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static ILambdaContext CreateContext()
    {
        var context = new Mock<ILambdaContext>();
        context.Setup(value => value.Logger).Returns(Mock.Of<ILambdaLogger>());
        return context.Object;
    }

    private static string AwsS3EventJson(params (string Bucket, string Key, string? Version, string? Sequencer)[] records) =>
        JsonSerializer.Serialize(new
        {
            Records = records.Select(record => new
            {
                eventName = "ObjectCreated:Put",
                s3 = new
                {
                    bucket = new { name = record.Bucket },
                    @object = new
                    {
                        key = record.Key,
                        versionId = record.Version,
                        sequencer = record.Sequencer,
                    },
                },
            }),
        });

    private static string BackfillJson(DateOnly? from = null, DateOnly? to = null)
    {
        var request = new BackfillRequest { Mode = "backfill", From = from, To = to };
        return JsonSerializer.Serialize(request);
    }

    [Test]
    public async Task HandleAsync_LiteralAwsS3Event_PassesDecodedSourceMetadata()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();

        await function.HandleAsync(
            ToStream(AwsS3EventJson(("my-bucket", "path/to+report.csv", "version-1", "000A"))),
            context);

        mock.Verify(service => service.ProcessCsvAsync(
            It.Is<IngestSource>(source =>
                source.Bucket == "my-bucket" &&
                source.Key == "path/to report.csv" &&
                source.VersionId == "version-1" &&
                source.Sequencer == "000A"),
            context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_S3Event_MultipleRecords_ProcessesEach()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();

        await function.HandleAsync(
            ToStream(AwsS3EventJson(("b1", "k1", null, "1"), ("b2", "k2", null, "2"))),
            context);

        mock.Verify(service => service.ProcessCsvAsync(
            It.Is<IngestSource>(source => source.Bucket == "b1" && source.Key == "k1"),
            context), Times.Once);
        mock.Verify(service => service.ProcessCsvAsync(
            It.Is<IngestSource>(source => source.Bucket == "b2" && source.Key == "k2"),
            context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_FirstS3RecordFails_ProcessesLaterRecordsThenThrows()
    {
        var mock = new Mock<IIngestService>();
        var context = CreateContext();
        mock.Setup(service => service.ProcessCsvAsync(
                It.Is<IngestSource>(source => source.Key == "bad.csv"),
                context))
            .ThrowsAsync(new InvalidDataException("bad report"));
        var function = new Function(mock.Object);

        await Assert.That(() => function.HandleAsync(
                ToStream(AwsS3EventJson(("bucket", "bad.csv", null, "1"), ("bucket", "good.csv", null, "2"))),
                context))
            .Throws<AggregateException>();

        mock.Verify(service => service.ProcessCsvAsync(
            It.Is<IngestSource>(source => source.Key == "good.csv"),
            context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_DispatchesToBackfill()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();

        await function.HandleAsync(ToStream(BackfillJson()), context);

        mock.Verify(service => service.ProcessBackfillAsync(null, null, context, null), Times.Once);
        mock.Verify(service => service.ProcessCsvAsync(
            It.IsAny<IngestSource>(),
            It.IsAny<ILambdaContext?>()), Times.Never);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithBounds_PassesBounds()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();
        var from = new DateOnly(2026, 6, 20);
        var to = new DateOnly(2026, 7, 10);

        await function.HandleAsync(ToStream(BackfillJson(from, to)), context);

        mock.Verify(service => service.ProcessBackfillAsync(from, to, context, null), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithFromOnly_PassesNullTo()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();
        var from = new DateOnly(2026, 6, 20);

        await function.HandleAsync(ToStream(BackfillJson(from, null)), context);

        mock.Verify(service => service.ProcessBackfillAsync(from, null, context, null), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithToOnly_PassesNullFrom()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = CreateContext();
        var to = new DateOnly(2026, 7, 10);

        await function.HandleAsync(ToStream(BackfillJson(null, to)), context);

        mock.Verify(service => service.ProcessBackfillAsync(null, to, context, null), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_InvertedBounds_Throws()
    {
        var function = new Function(Mock.Of<IIngestService>());

        await Assert.That(() => function.HandleAsync(
                ToStream(BackfillJson(new DateOnly(2026, 7, 10), new DateOnly(2026, 7, 1))),
                CreateContext()))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"mode\":\"backfil\"}")]
    [Arguments("{\"Records\":[]}")]
    public async Task HandleAsync_UnsupportedPayload_Throws(string json)
    {
        var function = new Function(Mock.Of<IIngestService>());

        await Assert.That(() => function.HandleAsync(ToStream(json), CreateContext()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task HandleAsync_MalformedS3Record_DoesNotCallIngest()
    {
        var ingest = new Mock<IIngestService>();
        var function = new Function(ingest.Object);

        await Assert.That(() => function.HandleAsync(
                ToStream(AwsS3EventJson(("", "key.csv", null, "1"))),
                CreateContext()))
            .Throws<AggregateException>();

        ingest.Verify(service => service.ProcessCsvAsync(
            It.IsAny<IngestSource>(),
            It.IsAny<ILambdaContext?>()), Times.Never);
    }

    [Test]
    public async Task HandleAsync_DispatchFailure_LogsStructuredInvocationError()
    {
        var logger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(value => value.Logger).Returns(logger.Object);
        var function = new Function(Mock.Of<IIngestService>());

        await Assert.That(() => function.HandleAsync(ToStream("{}"), context.Object))
            .Throws<InvalidOperationException>();

        logger.Verify(value => value.LogError(It.Is<string>(message =>
            message.Contains("\"event\":\"invocation_error\"", StringComparison.Ordinal) &&
            message.Contains("\"details\":", StringComparison.Ordinal))), Times.Once);
    }

    [Test]
    public async Task HandleAsync_NullJson_ThrowsInvalidOperationException()
    {
        var function = new Function(Mock.Of<IIngestService>());

        await Assert.That(() => function.HandleAsync(ToStream("null"), CreateContext()))
            .Throws<InvalidOperationException>();
    }
}
