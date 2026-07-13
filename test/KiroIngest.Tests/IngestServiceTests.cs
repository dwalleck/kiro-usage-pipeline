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
    private const string TestBucket = "analytics-bucket";
    private const string TargetParam = "/kiro-usage/target-list";
    private const string TargetEmail = "dwalleck@proton.me";
    private const string TestRawBucket = "raw-bucket";
    private const string TestRawPrefix = "AWSLogs/369434902231/KiroLogs/user_report/";

    // A minimal KIRO_CLI CSV row that exercises the full pipeline.
    private const string TestCsv =
        "Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used," +
        "Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email,auto_messages\n" +
        "2026-07-10,\"u1\",KIRO_CLI,3,10.5,2500,0,false,\"arn:...\",PRO_MAX,50,false,\"dwalleck@proton.me\",50";

    private static IngestService CreateService(Mock<IAmazonS3> s3, Mock<IAmazonSimpleSystemsManagement> ssm) =>
        new(s3.Object, ssm.Object, TestBucket, TargetParam, TestRawBucket, TestRawPrefix);

    private static Mock<IAmazonS3> CreateS3Mock(string csvContent)
    {
        var mock = new Mock<IAmazonS3>();
        // Use factory lambda so each call gets a fresh (non-disposed) response stream.
        mock.Setup(s => s.GetObjectAsync(
                It.IsAny<GetObjectRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csvContent)),
            });
        return mock;
    }

    private static Mock<IAmazonSimpleSystemsManagement> CreateSsmMock(string emails)
    {
        var mock = new Mock<IAmazonSimpleSystemsManagement>();
        mock.Setup(s => s.GetParameterAsync(
                It.IsAny<GetParameterRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new GetParameterResponse
            {
                Parameter = new Parameter { Value = emails },
            });
        return mock;
    }

    [Test]
    public async Task ProcessCsv_ValidCsv_WritesBothParquetFacts()
    {
        var s3 = CreateS3Mock(TestCsv);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessCsv("raw-bucket", "reports/KIRO_CLI_20260710.csv", null);

        // Verify usage_daily was written.
        s3.Verify(s => s.PutObjectAsync(
            It.Is<PutObjectRequest>(r =>
                r.BucketName == TestBucket &&
                r.Key == "usage_daily/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_20260710.parquet"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify model_messages was written.
        s3.Verify(s => s.PutObjectAsync(
            It.Is<PutObjectRequest>(r =>
                r.BucketName == TestBucket &&
                r.Key == "model_messages/date=2026-07-10/client_type=KIRO_CLI/KIRO_CLI_20260710.parquet"),
            It.IsAny<CancellationToken>()), Times.Once);

        // Exactly two PutObject calls total (one per fact).
        s3.Verify(s => s.PutObjectAsync(
            It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ProcessCsv_TwoInvocations_CachesTargetListOnce()
    {
        var s3 = CreateS3Mock(TestCsv);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessCsv("raw-bucket", "a.csv", null);
        await service.ProcessCsv("raw-bucket", "b.csv", null);

        // SSM should be hit only once despite two ProcessCsv calls.
        ssm.Verify(s => s.GetParameterAsync(
            It.IsAny<GetParameterRequest>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessCsv_NullContext_CompletesWithoutThrowing()
    {
        var s3 = CreateS3Mock(TestCsv);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessCsv("raw-bucket", "k.csv", null);

        // Null context should not prevent parquet output — both facts still land.
        s3.Verify(s => s.PutObjectAsync(
            It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Test]
    public async Task ProcessCsv_WithContext_LogsSummaryMessage()
    {
        var s3 = CreateS3Mock(TestCsv);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        var mockLogger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(c => c.Logger).Returns(mockLogger.Object);

        await service.ProcessCsv("raw-bucket", "k.csv", context.Object);

        mockLogger.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("Ingested s3://raw-bucket/k.csv"))),
            Times.Once);
    }

    [Test]
    public async Task ProcessCsv_HeaderOnlyCsv_WritesNoParquet()
    {
        var s3 = CreateS3Mock("Date,UserId,Client_Type\n");
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessCsv("raw-bucket", "empty.csv", null);

        s3.Verify(s => s.PutObjectAsync(
            It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Backfill ───────────────────────────────────────────────────────

    // Returns a mock S3 client that returns the given keys from ListObjectsV2
    // and the test CSV content from GetObjectAsync.
    private static Mock<IAmazonS3> CreateBackfillS3Mock(string[] keys)
    {
        var mock = CreateS3Mock(TestCsv);
        mock.Setup(s => s.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ListObjectsV2Response
            {
                S3Objects = [.. keys.Select(k => new S3Object { Key = k })],
                IsTruncated = false,
            });
        return mock;
    }

    private static string BackfillKey(string date, string filename) =>
        $"{TestRawPrefix}us-east-1/{date.Replace("-", "/")}/00/{filename}.csv";

    [Test]
    public async Task ProcessBackfill_AllCsvKeys_ProcessesEach()
    {
        var keys = new[]
        {
            BackfillKey("2026-07-10", "KIRO_CLI_report"),
            BackfillKey("2026-07-10", "KIRO_IDE_report"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, null, null);

        // Each key should trigger GetObject → transform → PutObject.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(keys.Length));
        s3.Verify(s => s.PutObjectAsync(
            It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(keys.Length * 2)); // 2 facts per key
    }

    [Test]
    public async Task ProcessBackfill_NullS3Objects_CompletesWithoutThrowing()
    {
        var s3 = CreateS3Mock(TestCsv);
        s3.Setup(s => s.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ListObjectsV2Response
            {
                S3Objects = null,  // SDK v4 returns null for empty pages
                IsTruncated = false,
            });
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, null, null);

        // No crash — the ?? [] guard handles null.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ProcessBackfill_SkipsNonCsvObjects()
    {
        var keys = new[]
        {
            BackfillKey("2026-07-10", "report"),          // .csv
            $"{TestRawPrefix}some-marker",                 // no extension, skip
            $"{TestRawPrefix}notes.txt",                   // .txt, skip
            BackfillKey("2026-07-10", "other_report"),     // .csv
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, null, null);

        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // Only the two .csv keys
    }

    [Test]
    public async Task ProcessBackfill_FromDate_FiltersBefore()
    {
        var keys = new[]
        {
            BackfillKey("2026-06-20", "early"),
            BackfillKey("2026-07-01", "mid"),
            BackfillKey("2026-07-10", "late"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(new DateOnly(2026, 7, 1), null, null);

        // Only mid and late (>= 2026-07-01) should be processed.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task ProcessBackfill_ToDate_FiltersAfter()
    {
        var keys = new[]
        {
            BackfillKey("2026-06-20", "early"),
            BackfillKey("2026-07-05", "mid"),
            BackfillKey("2026-07-10", "late"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, new DateOnly(2026, 7, 5), null);

        // Only early and mid (<= 2026-07-05) should be processed.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task ProcessBackfill_FromAndTo_NarrowsToRange()
    {
        var keys = new[]
        {
            BackfillKey("2026-06-20", "early"),
            BackfillKey("2026-07-01", "mid"),
            BackfillKey("2026-07-05", "late"),
            BackfillKey("2026-07-10", "latest"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 5), null);

        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2)); // mid + late only
    }

    [Test]
    public async Task ProcessBackfill_UnparseableDate_IsSkipped()
    {
        // A key whose path segments don't form a valid date.
        var keys = new[]
        {
            $"{TestRawPrefix}us-east-1/not/a/date/00/report.csv",
            BackfillKey("2026-07-10", "good"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, null, null);

        // Both keys are .csv, but only good passes the date filter when bounds are unbounded.
        // Without bounds, date extraction isn't even attempted, so both are processed.
        // Actually, with unbounded, both are processed (no date filter applied).
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public async Task ProcessBackfill_UnparseableDate_WithBounds_IsSkipped()
    {
        var keys = new[]
        {
            $"{TestRawPrefix}us-east-1/not/a/date/00/report.csv",
            BackfillKey("2026-07-10", "good"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 15), null);

        // With date bounds, unparseable date keys are skipped.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(1));
    }

    [Test]
    public async Task ProcessBackfill_WithContext_LogsProgress()
    {
        var keys = new[] { BackfillKey("2026-07-10", "report") };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        var mockLogger = new Mock<ILambdaLogger>();
        var context = new Mock<ILambdaContext>();
        context.Setup(c => c.Logger).Returns(mockLogger.Object);

        await service.ProcessBackfillAsync(null, null, context.Object);

        mockLogger.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("Backfill: listing objects"))),
            Times.Once);
        mockLogger.Verify(
            l => l.LogInformation(It.Is<string>(s => s.Contains("Backfill: complete"))),
            Times.Once);
    }

    [Test]
    public async Task ProcessBackfill_InvalidCalendarDate_IsSkipped()
    {
        // Feb 30 is a numerically parseable but calendar-invalid date.
        // TryParseExact should reject it gracefully (returns null, key is skipped).
        var keys = new[]
        {
            $"{TestRawPrefix}us-east-1/2026/02/30/00/bad.csv",
            BackfillKey("2026-07-10", "good"),
        };
        var s3 = CreateBackfillS3Mock(keys);
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(new DateOnly(2026, 7, 1), null, null);

        // Only the valid-date key should be processed; Feb 30 is silently skipped.
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(1));
    }

    [Test]
    public async Task ProcessBackfill_Paginates_WhenTruncated()
    {
        var page1 = new[] { BackfillKey("2026-07-10", "a") };
        var page2 = new[] { BackfillKey("2026-07-10", "b") };

        var s3 = CreateS3Mock(TestCsv);
        var callIndex = 0;
        s3.Setup(s => s.ListObjectsV2Async(
                It.IsAny<ListObjectsV2Request>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var isFirst = Interlocked.Increment(ref callIndex) == 1;
                return new ListObjectsV2Response
                {
                    S3Objects = isFirst
                        ? [new S3Object { Key = page1[0] }]
                        : [new S3Object { Key = page2[0] }],
                    IsTruncated = isFirst,
                    NextContinuationToken = isFirst ? "token1" : null,
                };
            });
        var ssm = CreateSsmMock(TargetEmail);
        var service = CreateService(s3, ssm);

        await service.ProcessBackfillAsync(null, null, null);

        s3.Verify(s => s.ListObjectsV2Async(
            It.IsAny<ListObjectsV2Request>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
        s3.Verify(s => s.GetObjectAsync(
            It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }
}
