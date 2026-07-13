using Amazon.S3;
using Amazon.SimpleSystemsManagement;

namespace KiroIngest;

// Builds an IngestService from the Lambda environment. The AWS clients pick up the
// region and execution-role credentials from the runtime automatically.
public static class IngestServiceFactory
{
    public const string AnalyticsBucketEnv = "ANALYTICS_BUCKET";
    public const string TargetListParameterEnv = "TARGET_LIST_PARAMETER";
    public const string RawBucketEnv = "RAW_BUCKET";
    public const string RawPrefixEnv = "RAW_PREFIX";

    public static IngestService FromEnvironment()
    {
        var analyticsBucket = Require(AnalyticsBucketEnv);
        var targetListParameter = Require(TargetListParameterEnv);
        var rawBucket = Require(RawBucketEnv);
        var rawPrefix = Require(RawPrefixEnv);

        return new IngestService(
            new AmazonS3Client(),
            new AmazonSimpleSystemsManagementClient(),
            analyticsBucket,
            targetListParameter,
            rawBucket,
            rawPrefix);
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name)
            ?? throw new InvalidOperationException($"Missing required environment variable: {name}");
}
