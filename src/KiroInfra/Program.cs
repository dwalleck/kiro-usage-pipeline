using Amazon.CDK;

namespace KiroInfra
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var app = new App();
            var account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT");
            var region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION");
            var applicationEnvironment = new Amazon.CDK.Environment
            {
                Account = account,
                Region = region,
            };

            new KiroInfraStack(app, "KiroInfraStack", new StackProps
            {
                Env = applicationEnvironment,
            });

            // IAM Identity Center stores groups in its home Region. The separate stack
            // has no cross-Region CloudFormation import; its output IDs are passed as
            // explicit parameters to the temporary us-east-1 integration-spike stack.
            new IdentityFoundationStack(app, "KiroIdentityFoundationStack", new StackProps
            {
                Env = new Amazon.CDK.Environment
                {
                    Account = account,
                    Region = "us-east-2",
                },
            });

            new GrafanaIntegrationSpikeStack(app, "KiroGrafanaIntegrationSpikeStack", new StackProps
            {
                Env = new Amazon.CDK.Environment
                {
                    Account = account,
                    Region = "us-east-1",
                },
            });

            app.Synth();
        }
    }
}
