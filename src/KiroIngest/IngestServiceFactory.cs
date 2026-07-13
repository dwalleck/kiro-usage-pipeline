using Amazon.Lambda;
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
    public const string FunctionNameEnv = "AWS_LAMBDA_FUNCTION_NAME";

    public static IngestService FromEnvironment()
    {
        var analyticsBucket = Require(AnalyticsBucketEnv);
        var targetListParameter = Require(TargetListParameterEnv);
        var rawBucket = Require(RawBucketEnv);
        var rawPrefix = Require(RawPrefixEnv);
        var functionName = Require(FunctionNameEnv);

        return new IngestService(
            new AmazonS3Client(),
            new AmazonSimpleSystemsManagementClient(),
            new LambdaBackfillContinuationScheduler(new AmazonLambdaClient(), functionName),
            analyticsBucket,
            targetListParameter,
            rawBucket,
            rawPrefix);
    }

    private static string Require(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required environment variable: {name}");
        }

        return value;
    }
}
