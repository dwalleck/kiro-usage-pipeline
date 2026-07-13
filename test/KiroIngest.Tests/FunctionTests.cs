using System.Text;
using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.S3Events;
using KiroIngest;
using Moq;

namespace KiroIngest.Tests;

public class FunctionTests
{
    private static Stream ToStream(string json) =>
        new MemoryStream(Encoding.UTF8.GetBytes(json));

    private static string S3EventJson(string bucket, string key)
    {
        var evnt = new S3Event
        {
            Records =
            [
                new S3Event.S3EventNotificationRecord
                {
                    S3 = new S3Event.S3Entity
                    {
                        Bucket = new S3Event.S3BucketEntity { Name = bucket },
                        Object = new S3Event.S3ObjectEntity { Key = key },
                    },
                },
            ],
        };
        return JsonSerializer.Serialize(evnt);
    }

    private static string BackfillJson(DateOnly? from = null, DateOnly? to = null)
    {
        var req = new BackfillRequest { Mode = "backfill", From = from, To = to };
        return JsonSerializer.Serialize(req);
    }

    // ── Live (S3 event) path ──────────────────────────────────────────

    [Test]
    public async Task HandleAsync_S3Event_SingleRecord_PassesBucketAndKey()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();

        await function.HandleAsync(ToStream(S3EventJson("my-bucket", "path/to/report.csv")), context);

        mock.Verify(s => s.ProcessCsv("my-bucket", "path/to/report.csv", context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_S3Event_MultipleRecords_ProcessesEach()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();
        var evnt = new S3Event
        {
            Records =
            [
                new S3Event.S3EventNotificationRecord
                {
                    S3 = new S3Event.S3Entity
                    {
                        Bucket = new S3Event.S3BucketEntity { Name = "b1" },
                        Object = new S3Event.S3ObjectEntity { Key = "k1" },
                    },
                },
                new S3Event.S3EventNotificationRecord
                {
                    S3 = new S3Event.S3Entity
                    {
                        Bucket = new S3Event.S3BucketEntity { Name = "b2" },
                        Object = new S3Event.S3ObjectEntity { Key = "k2" },
                    },
                },
            ],
        };

        await function.HandleAsync(ToStream(JsonSerializer.Serialize(evnt)), context);

        mock.Verify(s => s.ProcessCsv("b1", "k1", context), Times.Once);
        mock.Verify(s => s.ProcessCsv("b2", "k2", context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_S3Event_UrlEncodedKey_DecodesPlusSigns()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();
        // S3 encodes spaces as '+' in event keys.

        await function.HandleAsync(ToStream(S3EventJson("bucket", "path/of+the/report.csv")), context);

        mock.Verify(s => s.ProcessCsv("bucket", "path/of the/report.csv", context), Times.Once);
    }

    // ── Backfill path ─────────────────────────────────────────────────

    [Test]
    public async Task HandleAsync_BackfillMode_DispatchesToBackfill()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();

        await function.HandleAsync(ToStream(BackfillJson()), context);

        mock.Verify(s => s.ProcessBackfillAsync(null, null, context), Times.Once);
        mock.Verify(s => s.ProcessCsv(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ILambdaContext?>()), Times.Never);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithBounds_PassesBounds()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();
        var from = new DateOnly(2026, 6, 20);
        var to = new DateOnly(2026, 7, 10);

        await function.HandleAsync(ToStream(BackfillJson(from, to)), context);

        mock.Verify(s => s.ProcessBackfillAsync(from, to, context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithFromOnly_PassesNullTo()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();
        var from = new DateOnly(2026, 6, 20);

        await function.HandleAsync(ToStream(BackfillJson(from, null)), context);

        mock.Verify(s => s.ProcessBackfillAsync(from, null, context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_BackfillMode_WithToOnly_PassesNullFrom()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();
        var to = new DateOnly(2026, 7, 10);

        await function.HandleAsync(ToStream(BackfillJson(null, to)), context);

        mock.Verify(s => s.ProcessBackfillAsync(null, to, context), Times.Once);
    }

    [Test]
    public async Task HandleAsync_InvalidJson_ThrowsInvalidOperationException()
    {
        var mock = new Mock<IIngestService>();
        var function = new Function(mock.Object);
        var context = Mock.Of<ILambdaContext>();

        // JSON literal null — Deserialize returns null, null guard throws.
        await Assert.That(() => function.HandleAsync(ToStream("null"), context))
            .Throws<InvalidOperationException>();
    }
}
