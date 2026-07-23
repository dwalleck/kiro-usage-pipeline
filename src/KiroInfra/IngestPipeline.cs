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
            var region = Stack.Of(this).Region;

            // Explicit physical name (same convention as the DLQ): the backfill
            // continuation self-invoke permission must name the function as a plain
            // ARN string — referencing FunctionArn from the role's own default policy
            // creates a CloudFormation circular dependency (Function DependsOn
            // DefaultPolicy, DefaultPolicy GetAtt Function).
            var functionName = $"kiro-ingest-{account}";

            // Kiro writes reports under <bucket>/AWSLogs/<account>/KiroLogs/user_report/...
            // (point Kiro's report location at the raw bucket root). Filtering to this
            // prefix + .csv skips the stray UUID markers and the by_user_analytic reports.
            var userReportPrefix = $"AWSLogs/{account}/KiroLogs/user_report/";

            Function = new Function(this, "Function", new FunctionProps
            {
                Runtime = DotNet10,
                Architecture = Architecture.X86_64,
                FunctionName = functionName,
                Handler = "KiroIngest",
                MemorySize = 512,
                Timeout = Duration.Minutes(15),
                ReservedConcurrentExecutions = 1,
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

            // Spec §7.3 role, hand-rolled: the Grant helpers emit unconditioned s3:List*
            // (plus s3:GetBucket*) on the whole bucket, so listing is scoped here with
            // s3:prefix conditions instead. GetObjectVersion covers the version-pinned
            // live reads on the versioned raw bucket. No Glue or Athena access.
            Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["s3:GetObject", "s3:GetObjectVersion"],
                Resources = [rawBucket.ArnForObjects($"{userReportPrefix}*")],
            }));
            Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["s3:ListBucket"],
                Resources = [rawBucket.BucketArn],
                Conditions = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object>
                    {
                        ["s3:prefix"] = $"{userReportPrefix}*",
                    },
                },
            }));

            // Curated facts + sequencer state: object read/write/delete for source-output
            // reconciliation, with listing conditioned to the two fact prefixes the
            // reconciler scans.
            Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
                Resources =
                [
                    analyticsBucket.ArnForObjects("usage_daily/*"),
                    analyticsBucket.ArnForObjects("model_messages/*"),
                    analyticsBucket.ArnForObjects("ingest-state/*"),
                ],
            }));
            Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["s3:ListBucket"],
                Resources = [analyticsBucket.BucketArn],
                Conditions = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object>
                    {
                        ["s3:prefix"] = new[] { "usage_daily/*", "model_messages/*" },
                    },
                },
            }));

            // CMK path (spec §7.2): when UseCustomKey enables bucket keys, grant the
            // additive KMS access the Grant helpers previously wired implicitly.
            rawBucket.EncryptionKey?.GrantDecrypt(Function);
            analyticsBucket.EncryptionKey?.GrantEncryptDecrypt(Function);

            targetList.GrantRead(Function);
            Function.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["lambda:InvokeFunction"],
                Resources = [$"arn:aws:lambda:{region}:{account}:function:{functionName}"],
            }));

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
            // The execution role needs sqs:SendMessage on the DLQ: Lambda validates
            // this when the EventInvokeConfig destination is created.
            DeadLetterQueue.GrantSendMessages(Function);

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
