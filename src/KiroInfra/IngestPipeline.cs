using Amazon.CDK;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Notifications;
using Amazon.CDK.AWS.SSM;
using Constructs;
using System.Collections.Generic;
using AssetOptions = Amazon.CDK.AWS.S3.Assets.AssetOptions;

namespace KiroInfra
{
    // The live ingest path: a .NET 10 zip Lambda triggered by ObjectCreated on the raw
    // bucket (user_report/ prefix + .csv suffix). It reads the report, filters to the
    // Target List, Unpivots, and writes usage_daily + model_messages Parquet to the
    // analytics bucket. Packaged Docker-free via `dotnet publish` local bundling.
    public class IngestPipeline : Construct
    {
        // Managed .NET 10 Lambda runtime (GA Jan 2026). Built via the string constructor
        // so it works regardless of whether the CDK version exposes a Runtime.DOTNET_10.
        private static readonly Runtime DotNet10 = new("dotnet10");

        private const string LambdaProjectPath = "src/KiroIngest";
        private const string PublishRuntime = "linux-x64";

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

            var function = new Function(this, "Function", new FunctionProps
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
                        // Docker image is the fallback only; local bundling handles the build.
                        Image = DockerImage.FromRegistry("mcr.microsoft.com/dotnet/sdk:10.0"),
                        Local = new DotnetLambdaBundling(LambdaProjectPath, PublishRuntime),
                    },
                }),
                Environment = new Dictionary<string, string>
                {
                    ["ANALYTICS_BUCKET"] = analyticsBucket.BucketName,
                    ["TARGET_LIST_PARAMETER"] = targetList.ParameterName,
                },
            });

            // Least-privilege grants (spec 7.3): prefix-scoped read on raw; writes limited
            // to the two curated prefixes; read the Target List parameter. No KMS, no Glue.
            rawBucket.GrantRead(function, $"{userReportPrefix}*");
            analyticsBucket.GrantWrite(function, "usage_daily/*");
            analyticsBucket.GrantWrite(function, "model_messages/*");
            targetList.GrantRead(function);

            // Event-driven trigger (spec 7.4). CDK adds the scoped s3.amazonaws.com invoke
            // permission automatically.
            rawBucket.AddEventNotification(
                EventType.OBJECT_CREATED,
                new LambdaDestination(function),
                new NotificationKeyFilter { Prefix = userReportPrefix, Suffix = ".csv" });
        }
    }
}
