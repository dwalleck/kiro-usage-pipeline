using Amazon.CDK;
using Amazon.CDK.AWS.Grafana;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;
using System.Collections.Generic;

namespace KiroInfra
{
    // Managed Grafana workspace for the Kiro usage dashboards, wired to the
    // Athena workgroup via a scoped data-source IAM role. Auth: IAM Identity
    // Center (the account must have an Identity Center instance enabled).
    // Dashboards are reconciled from the committed JSON by GrafanaProvisioning
    // on every deploy.
    public class GrafanaWorkspace : Construct
    {
        public CfnWorkspace Workspace { get; }
        public Role DataSourceRole { get; }
        public const string WorkspaceName = "Kiro-Usage";

        public GrafanaWorkspace(
            Construct scope,
            string id,
            IBucket analyticsBucket,
            string athenaWorkGroupName,
            string workspaceName = WorkspaceName,
            string workspaceDescription = "Kiro Usage Dashboard — usage analytics over Athena facts") : base(scope, id)
        {
            var account = Stack.Of(this).Account;
            var region = Stack.Of(this).Region;
            var resultsPrefix = $"arn:aws:s3:::{analyticsBucket.BucketName}/athena-results/*";
            var curatedPrefixes = new[]
            {
                $"arn:aws:s3:::{analyticsBucket.BucketName}/usage_daily/*",
                $"arn:aws:s3:::{analyticsBucket.BucketName}/model_messages/*",
            };

            // Data-source role (spec §7.6). Trust: grafana.amazonaws.com with a
            // confused-deputy guard. Managed AmazonGrafanaAthenaAccess plus a scoped
            // inline policy pinning the kiro-usage workgroup, Glue reads on kiro_usage
            // tables, curated S3 reads, and query-results read/write.
            DataSourceRole = new Role(this, "DataSourceRole", new RoleProps
            {
                AssumedBy = new ServicePrincipal("grafana.amazonaws.com", new ServicePrincipalOpts
                {
                    Conditions = new Dictionary<string, object>
                    {
                        ["StringEquals"] = new Dictionary<string, string>
                        {
                            ["aws:SourceAccount"] = account,
                        },
                    },
                }),
                Description = "Grafana data-source role: scoped Athena+Glue+S3 on kiro_usage",
            });

            DataSourceRole.AddManagedPolicy(
                ManagedPolicy.FromAwsManagedPolicyName("service-role/AmazonGrafanaAthenaAccess"));

            DataSourceRole.AttachInlinePolicy(new Policy(this, "ScopedAthenaPolicy", new PolicyProps
            {
                Document = new PolicyDocument(new PolicyDocumentProps
                {
                    Statements = new[]
                    {
                        // Pin all Athena actions to the kiro-usage workgroup.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[]
                            {
                                "athena:StartQueryExecution",
                                "athena:StopQueryExecution",
                                "athena:GetQueryExecution",
                                "athena:GetQueryResults",
                                "athena:GetWorkGroup",
                            },
                            Resources = new[]
                            {
                                $"arn:aws:athena:{region}:{account}:workgroup/{athenaWorkGroupName}",
                            },
                        }),

                        // Read Glue metadata for the two fact tables and the database.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[]
                            {
                                "glue:GetDatabase",
                                "glue:GetTable",
                                "glue:GetTables",
                                "glue:GetPartitions",
                            },
                            Resources = new[]
                            {
                                $"arn:aws:glue:{region}:{account}:catalog",
                                $"arn:aws:glue:{region}:{account}:database/{QueryLayer.DatabaseName}",
                                $"arn:aws:glue:{region}:{account}:table/{QueryLayer.DatabaseName}/usage_daily",
                                $"arn:aws:glue:{region}:{account}:table/{QueryLayer.DatabaseName}/model_messages",
                            },
                        }),

                        // Read curated Parquet from the two fact prefixes.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:GetObject" },
                            Resources = curatedPrefixes,
                        }),

                        // Athena validates the existing result bucket and may use
                        // multipart uploads for large result sets.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:GetBucketLocation", "s3:ListBucketMultipartUploads" },
                            Resources = new[] { analyticsBucket.BucketArn },
                        }),

                        // List the analytics bucket so Athena can discover partitions.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:ListBucket" },
                            Resources = new[] { analyticsBucket.BucketArn },
                            Conditions = new Dictionary<string, object>
                            {
                                ["StringLike"] = new Dictionary<string, string>
                                {
                                    ["s3:prefix"] = "usage_daily/*",
                                },
                            },
                        }),

                        // Also allow listing model_messages/ and athena-results/ prefixes
                        // (S3 condition StringLike multi-value via two statements is cleaner
                        // than a complex ForAnyValue:StringLike).
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:ListBucket" },
                            Resources = new[] { analyticsBucket.BucketArn },
                            Conditions = new Dictionary<string, object>
                            {
                                ["StringLike"] = new Dictionary<string, string>
                                {
                                    ["s3:prefix"] = "model_messages/*",
                                },
                            },
                        }),

                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:ListBucket" },
                            Resources = new[] { analyticsBucket.BucketArn },
                            Conditions = new Dictionary<string, object>
                            {
                                ["StringLike"] = new Dictionary<string, string>
                                {
                                    ["s3:prefix"] = "athena-results/*",
                                },
                            },
                        }),

                        // Read/write Athena query results.
                        new PolicyStatement(new PolicyStatementProps
                        {
                            Effect = Effect.ALLOW,
                            Actions = new[] { "s3:GetObject", "s3:PutObject", "s3:AbortMultipartUpload", "s3:ListMultipartUploadParts" },
                            Resources = new[] { resultsPrefix },
                        }),
                    },
                }),
            }));

            // Managed Grafana workspace. IAM Identity Center authentication per spec —
            // the account must have an Identity Center instance enabled. For a POC
            // without Identity Center, the account owner can enable it in the IAM
            // Identity Center console (free tier) before deploying this stack.
            // PermissionType is CUSTOMER_MANAGED because we provide our own scoped role
            // rather than letting Grafana auto-create one (SERVICE_MANAGED).
            Workspace = new CfnWorkspace(this, "Workspace", new CfnWorkspaceProps
            {
                AccountAccessType = "CURRENT_ACCOUNT",
                AuthenticationProviders = new[] { "AWS_SSO" },
                PermissionType = "CUSTOMER_MANAGED",
                PluginAdminEnabled = true,
                RoleArn = DataSourceRole.RoleArn,
                Name = workspaceName,
                Description = workspaceDescription,
                GrafanaVersion = "10.4",
            });
        }
    }
}
