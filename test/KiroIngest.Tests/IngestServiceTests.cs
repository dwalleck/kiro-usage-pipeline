using System.Net;
using System.Text;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;
using KiroIngest;
using Moq;

namespace KiroIngest.Tests;

public class IngestServiceTests
{
    private const string AnalyticsBucket = "analytics-bucket";
    private const string TargetParameter = "/kiro-usage/target-list";
    private const string TargetEmail = "dwalleck@proton.me";
    private const string RawBucket = "raw-bucket";
    private const string RawPrefix = "AWSLogs/369434902231/KiroLogs/user_report/";

    private const string TestCsv =
        "Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used," +
        "Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email,auto_messages\n" +
        "2026-07-10,\"u1\",KIRO_CLI,3,10.5,2500,0,false,\"arn:...\",PRO_MAX,50,false,\"dwalleck@proton.me\",50";

    private static IngestService CreateService(
        Mock<IAmazonS3> s3,
        Mock<IAmazonSimpleSystemsManagement> ssm,
        IBackfillContinuationScheduler? continuationScheduler = null) =>
        new(
            s3.Object,
            ssm.Object,
            continuationScheduler ?? Mock.Of<IBackfillContinuationScheduler>(),
            AnalyticsBucket,
            TargetParameter,
            RawBucket,
            RawPrefix);

    private static Mock<IAmazonS3> CreateS3Mock(
        Func<GetObjectRequest, string>? csvResolver = null,
        string? savedSequencer = null)
    {
        csvResolver ??= _ => TestCsv;
        var mock = new Mock<IAmazonS3>();

        mock.Setup(client => client.GetObjectAsync(
                It.IsAny<GetObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .Returns((GetObjectRequest request, CancellationToken _) =>
            {
                if (request.BucketName == AnalyticsBucket && request.Key.StartsWith("ingest-state/", StringComparison.Ordinal))
                {
                    if (savedSequencer is null)
                    {
                        return Task.FromException<GetObjectResponse>(new AmazonS3Exception("not found")
                        {
                            StatusCode = HttpStatusCode.NotFound,
                            ErrorCode = "NoSuchKey",
                        });
                    }

                    return Task.FromResult(new GetObjectResponse
                    {
                        ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(
                            $"{{\"Sequencer\":\"{savedSequencer}\"}}")),
                    });
                }

                return Task.FromResult(new GetObjectResponse
                {
                    ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(csvResolver(request))),
                });
            });

        mock.Setup(client => client.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = [],
                IsTruncated = false,
            });

        mock.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());
        mock.Setup(client => client.DeleteObjectAsync(
                It.IsAny<DeleteObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());

        return mock;
    }

    private static Mock<IAmazonSimpleSystemsManagement> CreateSsmMock(string emails)
    {
        var mock = new Mock<IAmazonSimpleSystemsManagement>();
        mock.Setup(client => client.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetParameterResponse
            {
                Parameter = new Parameter { Value = emails },
            });
        return mock;
    }

    private static string BackfillKey(string date, string filename) =>
        $"{RawPrefix}us-east-1/{date.Replace("-", "/")}/00/{filename}.csv";

    private static string CsvForDate(string date) => TestCsv.Replace("2026-07-10", date, StringComparison.Ordinal);

    private static string DateFromBackfillKey(string key)
    {
        var parts = key.Split('/');
        return $"{parts[^5]}-{parts[^4]}-{parts[^3]}";
    }

    private static Mock<IAmazonS3> CreateBackfillS3Mock(string[] keys)
    {
        var mock = CreateS3Mock(request => CsvForDate(DateFromBackfillKey(request.Key)));
        mock.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == RawBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = [.. keys.Select(key => new S3Object { Key = key })],
                IsTruncated = false,
            });
        return mock;
    }

    [Test]
    public async Task ProcessCsv_ValidCsv_WritesBothFactsWithSourceIdentity()
    {
        var s3 = CreateS3Mock();
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessCsv(RawBucket, "reports/KIRO_CLI_20260710.csv");

        s3.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request =>
                request.Key.StartsWith("usage_daily/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_20260710-", StringComparison.Ordinal) &&
                request.Key.EndsWith(".parquet", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
        s3.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request =>
                request.Key.StartsWith("model_messages/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_20260710-", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessCsv_TwoInvocations_RefreshesTargetListEachTime()
    {
        var s3 = CreateS3Mock();
        var ssm = new Mock<IAmazonSimpleSystemsManagement>();
        ssm.SetupSequence(client => client.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetParameterResponse { Parameter = new Parameter { Value = TargetEmail } })
            .ReturnsAsync(new GetParameterResponse { Parameter = new Parameter { Value = "removed@example.com" } });
        var service = CreateService(s3, ssm);

        await service.ProcessCsv(RawBucket, "a.csv");
        await service.ProcessCsv(RawBucket, "b.csv");

        ssm.Verify(client => client.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ProcessCsv_NoLongerProducesRows_DeletesExistingOutputs()
    {
        var s3 = CreateS3Mock();
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == AnalyticsBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "usage_daily/date=2026-07-10/client_type=KIRO_CLI/report.parquet" },
                    new S3Object { Key = "model_messages/date=2026-07-10/client_type=KIRO_CLI/report.parquet" },
                ],
                IsTruncated = false,
            });
        var service = CreateService(s3, CreateSsmMock("someone-else@example.com"));

        await service.ProcessCsv(RawBucket, "reports/report.csv");

        s3.Verify(client => client.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        s3.Verify(client => client.PutObjectAsync(
            It.IsAny<PutObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessCsv_SecondFactWriteFails_RemovesAllSourceOutputs()
    {
        var s3 = CreateS3Mock();
        s3.SetupSequence(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse())
            .ThrowsAsync(new AmazonS3Exception("write failed"));
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessCsv(RawBucket, "report.csv"))
            .Throws<AmazonS3Exception>();

        s3.Verify(client => client.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ProcessCsv_WriteFailsAfterExistingOutputsFound_FailsClosedByDeletingGeneration()
    {
        var s3 = CreateS3Mock();
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == AnalyticsBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects =
                [
                    new S3Object { Key = "usage_daily/date=2026-07-10/client_type=KIRO_CLI/report.parquet" },
                    new S3Object { Key = "model_messages/date=2026-07-10/client_type=KIRO_CLI/report.parquet" },
                ],
                IsTruncated = false,
            });
        s3.SetupSequence(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse())
            .ThrowsAsync(new AmazonS3Exception("write failed"));
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessCsv(RawBucket, "reports/report.csv"))
            .Throws<AmazonS3Exception>();

        s3.Verify(client => client.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Exactly(4));
    }

    [Test]
    public async Task ProcessCsv_AnalyticsListingTruncatedWithoutToken_ThrowsBeforePublishing()
    {
        var s3 = CreateS3Mock();
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == AnalyticsBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = [],
                IsTruncated = true,
                NextContinuationToken = null,
            });
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessCsv(RawBucket, "report.csv"))
            .Throws<InvalidOperationException>();

        s3.Verify(client => client.PutObjectAsync(
            It.IsAny<PutObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        s3.Verify(client => client.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessCsv_VersionedSource_ReadsNotifiedVersion()
    {
        var s3 = CreateS3Mock();
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessCsv(new IngestSource(RawBucket, "report.csv", "version-7", "0A"));

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request =>
                request.BucketName == RawBucket &&
                request.Key == "report.csv" &&
                request.VersionId == "version-7"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessCsv_SequencerStateWriteFails_PreservesCompleteGeneration()
    {
        var s3 = CreateS3Mock();
        s3.Setup(client => client.PutObjectAsync(
                It.Is<PutObjectRequest>(request => request.Key.StartsWith("ingest-state/", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("state timeout"));
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessCsv(new IngestSource(
                RawBucket,
                "report.csv",
                "version-1",
                "0A")))
            .Throws<AmazonS3Exception>();

        s3.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request => request.Key.EndsWith(".parquet", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
        s3.Verify(client => client.DeleteObjectAsync(
            It.IsAny<DeleteObjectRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessCsv_OlderSequencer_SkipsWithoutReadingRawObject()
    {
        var s3 = CreateS3Mock(savedSequencer: "0A");
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessCsv(new IngestSource(RawBucket, "report.csv", "version-1", "09"));

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.BucketName == RawBucket),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessCsv_ReportDateDiffersFromKeyDate_WarnsAndStillWrites()
    {
        var key = BackfillKey("2026-07-09", "KIRO_CLI_mismatch");
        var s3 = CreateS3Mock();
        var service = CreateService(s3, CreateSsmMock(TargetEmail));
        var logger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(value => value.Logger).Returns(logger.Object);

        await service.ProcessCsv(new IngestSource(RawBucket, key), context.Object);

        logger.Verify(value => value.LogWarning(It.Is<string>(message =>
            message.Contains("\"event\":\"ingest_date_mismatch\"", StringComparison.Ordinal) &&
            message.Contains("\"key_date\":\"2026-07-09\"", StringComparison.Ordinal) &&
            message.Contains("\"report_dates\":[\"2026-07-10\"]", StringComparison.Ordinal))), Times.Once);
        s3.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request => request.Key.StartsWith(
                "usage_daily/date=2026-07-10/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_ReportDateDiffersFromKeyDate_ProcessesLikeLivePath()
    {
        var key = BackfillKey("2026-07-09", "KIRO_CLI_mismatch");
        var s3 = CreateS3Mock();
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == RawBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = key }],
                IsTruncated = false,
            });
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessBackfillAsync(null, null);

        s3.Verify(client => client.PutObjectAsync(
            It.Is<PutObjectRequest>(request => request.Key.StartsWith(
                "usage_daily/date=2026-07-10/", StringComparison.Ordinal)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessCsv_WithContext_LogsStructuredCounts()
    {
        var service = CreateService(CreateS3Mock(), CreateSsmMock(TargetEmail));
        var logger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(value => value.Logger).Returns(logger.Object);

        await service.ProcessCsv(RawBucket, "report.csv", context.Object);

        logger.Verify(value => value.LogInformation(It.Is<string>(message =>
            message.Contains("\"event\":\"ingest_complete\"", StringComparison.Ordinal) &&
            message.Contains("\"rows_read\":1", StringComparison.Ordinal) &&
            message.Contains("\"rows_kept\":1", StringComparison.Ordinal) &&
            message.Contains("\"usage_daily_rows\":1", StringComparison.Ordinal) &&
            message.Contains("\"model_messages_rows\":1", StringComparison.Ordinal))), Times.Once);
    }

    [Test]
    public async Task ProcessCsv_MalformedReport_LogsStructuredErrorAndThrows()
    {
        var malformed = "Date,User_Email\n2026-07-10,dwalleck@proton.me";
        var service = CreateService(CreateS3Mock(_ => malformed), CreateSsmMock(TargetEmail));
        var logger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(value => value.Logger).Returns(logger.Object);

        await Assert.That(() => service.ProcessCsv(RawBucket, "bad.csv", context.Object))
            .Throws<InvalidDataException>();

        logger.Verify(value => value.LogError(It.Is<string>(message =>
            message.Contains("\"event\":\"ingest_error\"", StringComparison.Ordinal) &&
            message.Contains("\"Key\":\"bad.csv\"", StringComparison.Ordinal))), Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_NullS3Objects_CompletesWithoutProcessing()
    {
        var s3 = CreateS3Mock();
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == RawBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = null,
                IsTruncated = false,
            });
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessBackfillAsync(null, null);

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.BucketName == RawBucket),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessBackfill_AllCsvKeys_ProcessesEach()
    {
        var keys = new[]
        {
            BackfillKey("2026-07-09", "a"),
            BackfillKey("2026-07-10", "b"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessBackfillAsync(null, null);

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.BucketName == RawBucket),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ProcessBackfill_DateBounds_ProcessOnlyRange()
    {
        var keys = new[]
        {
            BackfillKey("2026-06-20", "early"),
            BackfillKey("2026-07-01", "middle"),
            BackfillKey("2026-07-10", "late"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await service.ProcessBackfillAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5));

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.BucketName == RawBucket),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_PoisonObject_ProcessesLaterObjectsThenThrows()
    {
        var badKey = BackfillKey("2026-07-09", "bad");
        var goodKey = BackfillKey("2026-07-10", "good");
        var s3 = CreateBackfillS3Mock([badKey, goodKey]);
        s3.Setup(client => client.GetObjectAsync(
                It.Is<GetObjectRequest>(request => request.Key == badKey),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("poison"));
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessBackfillAsync(null, null))
            .Throws<AggregateException>();

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.Key == goodKey),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_InvalidKey_ContinuesThenThrowsAggregate()
    {
        var goodKey = BackfillKey("2026-07-10", "good");
        var s3 = CreateBackfillS3Mock([$"{RawPrefix}bad.csv", goodKey]);
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessBackfillAsync(null, null))
            .Throws<AggregateException>();

        s3.Verify(client => client.GetObjectAsync(
            It.Is<GetObjectRequest>(request => request.Key == goodKey),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_InvertedBounds_ThrowsBeforeListing()
    {
        var s3 = CreateS3Mock();
        var service = CreateService(s3, CreateSsmMock(TargetEmail));

        await Assert.That(() => service.ProcessBackfillAsync(
                new DateOnly(2026, 7, 10),
                new DateOnly(2026, 7, 1)))
            .Throws<ArgumentException>();

        s3.Verify(client => client.ListObjectsV2Async(
            It.IsAny<ListObjectsV2Request>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ProcessBackfill_TruncatedPage_SchedulesNextPage()
    {
        var firstKey = BackfillKey("2026-07-09", "first");
        var s3 = CreateS3Mock(request => CsvForDate(DateFromBackfillKey(request.Key)));
        s3.Setup(client => client.ListObjectsV2Async(
                It.Is<ListObjectsV2Request>(request => request.BucketName == RawBucket),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListObjectsV2Response
            {
                S3Objects = [new S3Object { Key = firstKey }],
                IsTruncated = true,
                NextContinuationToken = "next",
            });
        var scheduler = new Mock<IBackfillContinuationScheduler>();
        var service = CreateService(s3, CreateSsmMock(TargetEmail), scheduler.Object);

        await service.ProcessBackfillAsync(null, null);

        scheduler.Verify(value => value.ScheduleAsync(null, null, "next"), Times.Once);
        s3.Verify(client => client.ListObjectsV2Async(
            It.Is<ListObjectsV2Request>(request => request.BucketName == RawBucket),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
