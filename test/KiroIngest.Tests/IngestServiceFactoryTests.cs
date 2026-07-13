using KiroIngest;

namespace KiroIngest.Tests;

[NotInParallel("EnvironmentVariables")]
public class IngestServiceFactoryTests
{
    [Before(Test)]
    public void ClearEnv()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, null);
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, null);
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, null);
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, null);
        Environment.SetEnvironmentVariable(IngestServiceFactory.FunctionNameEnv, null);
    }

    [Test]
    public async Task FromEnvironment_MissingAnalyticsBucket_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, "/param");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, "raw");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, "pre/");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: ANALYTICS_BUCKET");
    }

    [Test]
    public async Task FromEnvironment_MissingTargetList_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, "my-bucket");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, "raw");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, "pre/");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: TARGET_LIST_PARAMETER");
    }

    [Test]
    public async Task FromEnvironment_MissingRawBucket_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, "analytics");
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, "/param");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, "pre/");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: RAW_BUCKET");
    }

    [Test]
    public async Task FromEnvironment_MissingRawPrefix_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, "analytics");
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, "/param");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, "raw");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: RAW_PREFIX");
    }

    [Test]
    public async Task FromEnvironment_MissingFunctionName_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, "analytics");
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, "/param");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, "raw");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, "pre/");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: AWS_LAMBDA_FUNCTION_NAME");
    }

    [Test]
    public async Task FromEnvironment_WhitespaceValue_Throws()
    {
        Environment.SetEnvironmentVariable(IngestServiceFactory.AnalyticsBucketEnv, "   ");
        Environment.SetEnvironmentVariable(IngestServiceFactory.TargetListParameterEnv, "/param");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawBucketEnv, "raw");
        Environment.SetEnvironmentVariable(IngestServiceFactory.RawPrefixEnv, "pre/");

        await Assert.That(() => IngestServiceFactory.FromEnvironment())
            .Throws<InvalidOperationException>()
            .WithMessage("Missing required environment variable: ANALYTICS_BUCKET");
    }

}
