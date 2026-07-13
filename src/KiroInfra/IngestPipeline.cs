using Amazon.CDK;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Notifications;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
using Constructs;
using System.Collections.Generic;
using AssetOptions = Amazon.CDK.AWS.S3.Assets.AssetOptions;

namespace KiroInfra
{
    // The live ingest path: a .NET 10 zip Lambda triggered by ObjectCreated on the raw
    // bucket (user_report/ prefix + .csv suffix). It reads the report, filters to the
    // Target List, Unpivots, and writes usage_daily + model_messages Parquet to the
    // analytics bucket. Packaged via Docker bundling with the .NET 10 SDK image.
    public class IngestPipeline : Construct
    {
        // Managed .NET 10 Lambda runtime (GA Jan 2026). Built via the string constructor
        // so it works regardless of whether the CDK version exposes a Runtime.DOTNET_10.
        private static readonly Runtime DotNet10 = new("dotnet10");

        private const string LambdaProjectPath = "src/KiroIngest";

        public Function Function { get; }

    public Queue DeadLetterQueue { get; }

    public Alarm ErrorAlarm { get; }

    public Alarm DlqDepthAlarm { get; }

        public IngestPipeline(
            Construct scope,
            string id,
            IBucket rawBucket,
            IBucket analyticsBucket,
            IStringListParameter targetList) : base(scope, id)
        {
            var account = Stack.Of(this).Account;

            // Kiro writes reports under <bucket>/AWSLogs/<account>/KiroLogs/user_report/...
            // (point Kiro's report location at the raw bucket root). Filtering to this
            // prefix + .csv skips the stray UUID markers and the by_user_analytic reports.
            var userReportPrefix = $"AWSLogs/{account}/KiroLogs/user_report/";

            Function = new Function(this, "Function", new FunctionProps
            {
                Runtime = DotNet10,
                Architecture = Architecture.X86_64,
                Handler = "KiroIngest",
                MemorySize = 512,
                Timeout = Duration.Seconds(60),
                Description = "Kiro usage ingest: User Activity Report CSV -> filtered/unpivoted Parquet",
                Code = Code.FromAsset(LambdaProjectPath, new AssetOptions
                {
                    // Hash the bundled output so the asset changes only when the build does.
                    AssetHashType = AssetHashType.OUTPUT,
                    Bundling = new BundlingOptions
                    {
                        Image = DockerImage.FromRegistry("mcr.microsoft.com/dotnet/sdk:10.0"),
                        Command =
                        [
                            "dotnet", "publish",
                            "-c", "Release",
                            "-r", "linux-x64",
                            "--no-self-contained",
                            "--nologo",
                            "-o", "/asset-output",
                        ],
                    },
                }),
                Environment = new Dictionary<string, string>
                {
                    ["ANALYTICS_BUCKET"] = analyticsBucket.BucketName,
                    ["TARGET_LIST_PARAMETER"] = targetList.ParameterName,
                    ["RAW_BUCKET"] = rawBucket.BucketName,
                    ["RAW_PREFIX"] = userReportPrefix,
                },
            });

            // Least-privilege grants (spec 7.3): prefix-scoped read on raw; writes limited
            // to the two curated prefixes; read the Target List parameter. No KMS, no Glue.
            rawBucket.GrantRead(Function, $"{userReportPrefix}*");
            analyticsBucket.GrantWrite(Function, "usage_daily/*");
            analyticsBucket.GrantWrite(Function, "model_messages/*");
            targetList.GrantRead(Function);

            // Event-driven trigger (spec 7.4). CDK adds the scoped s3.amazonaws.com invoke
            // permission automatically.
            rawBucket.AddEventNotification(
                EventType.OBJECT_CREATED,
                new LambdaDestination(Function),
                new NotificationKeyFilter { Prefix = userReportPrefix, Suffix = ".csv" });

            // ── Observability (ticket 13) ──────────────────────────────────

            // Dead-letter queue: captures events that exhaust Lambda retries.
            DeadLetterQueue = new Queue(this, "DeadLetterQueue", new QueueProps
            {
                QueueName = $"kiro-ingest-dlq-{account}",
                RetentionPeriod = Duration.Days(14),
                EnforceSSL = true,
                VisibilityTimeout = Duration.Seconds(Function.Timeout!.ToSeconds() * 6),
            });

            // Route failed async invocations (after retry exhaustion) to the DLQ.
            _ = new CfnEventInvokeConfig(this, "EventInvokeConfig", new CfnEventInvokeConfigProps
            {
                FunctionName = Function.FunctionName,
                Qualifier = "$LATEST",
                MaximumRetryAttempts = 2,
                DestinationConfig = new CfnEventInvokeConfig.DestinationConfigProperty
                {
                    OnFailure = new CfnEventInvokeConfig.OnFailureProperty
                    {
                        Destination = DeadLetterQueue.QueueArn,
                    },
                },
            });

            // The async invoke destination uses the Lambda service principal
            // (lambda.amazonaws.com), not the function execution role, to deliver
            // failed events to SQS. Add a resource policy so the service can send.
            DeadLetterQueue.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "AllowLambdaServiceToSend",
                Effect = Effect.ALLOW,
                Principals = [new ServicePrincipal("lambda.amazonaws.com")],
                Actions = ["sqs:SendMessage"],
                Resources = ["*"],
                Conditions = new Dictionary<string, object>
                {
                    ["ArnLike"] = new Dictionary<string, string>
                    {
                        ["aws:SourceArn"] = Function.FunctionArn,
                    },
                },
            }));

            // CloudWatch alarm: Lambda errors > 0 over two consecutive evaluation periods.
            ErrorAlarm = new Alarm(this, "LambdaErrorAlarm", new AlarmProps
            {
                AlarmName = $"kiro-ingest-errors-{account}",
                AlarmDescription = "Ingest Lambda errors exceeded threshold — check DLQ for failed reports",
                Metric = Function.MetricErrors(new MetricOptions
                {
                    Statistic = "Sum",
                    Period = Duration.Minutes(1),
                }),
                Threshold = 1,
                EvaluationPeriods = 2,
                ComparisonOperator = ComparisonOperator.GREATER_THAN_OR_EQUAL_TO_THRESHOLD,
                TreatMissingData = TreatMissingData.NOT_BREACHING,
            });

            // CloudWatch alarm: DLQ has messages waiting (something failed permanently).
            DlqDepthAlarm = new Alarm(this, "DlqDepthAlarm", new AlarmProps
            {
                AlarmName = $"kiro-ingest-dlq-depth-{account}",
                AlarmDescription = "Ingest DLQ has messages — reports failed to process after retry exhaustion",
                Metric = DeadLetterQueue.MetricApproximateNumberOfMessagesVisible(new MetricOptions
                {
                    Statistic = "Sum",
                    Period = Duration.Minutes(1),
                }),
                Threshold = 0,
                EvaluationPeriods = 1,
                ComparisonOperator = ComparisonOperator.GREATER_THAN_THRESHOLD,
                TreatMissingData = TreatMissingData.NOT_BREACHING,
            });
        }
    }
}
