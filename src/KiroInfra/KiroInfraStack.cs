using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.KMS;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SSM;
using Constructs;
using System.Collections.Generic;

namespace KiroInfra
{
    public class KiroInfraStack : Stack
    {
        // Athena query results are disposable; expire them so the analytics bucket
        // doesn't accumulate scan output. Curated fact prefixes are never expired.
        private const int AthenaResultsExpiryDays = 14;

        // Kiro writes to S3/KMS through the Amazon Q service principal.
        private const string KiroServicePrincipal = "q.amazonaws.com";

        internal KiroInfraStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            // Optional KMS key for bucket encryption (toggle UseCustomKey in cdk.json context to enable).
            // Off by default: both buckets use SSE-S3 and there are zero KMS statements anywhere.
            // When enabled, configure it in: Kiro Console > Settings > Encryption key.
            var useCustomKey = (string)Node.TryGetContext("UseCustomKey") == "true";
            Key encryptionKey = null;
            if (useCustomKey)
            {
                encryptionKey = new Key(this, "KiroEncryptionKey", new KeyProps
                {
                    Description = "KMS key for Kiro usage raw reports and analytics data",
                    EnableKeyRotation = true,
                    RemovalPolicy = RemovalPolicy.RETAIN,
                });
                encryptionKey.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
                {
                    Sid = "AllowKiroService",
                    Effect = Effect.ALLOW,
                    Principals = [new ServicePrincipal(KiroServicePrincipal)],
                    Actions = ["kms:GenerateDataKey", "kms:Decrypt"],
                    Resources = ["*"],
                    Conditions = KiroSourceConditions(),
                }));

                new CfnOutput(this, "EncryptionKeyArn", new CfnOutputProps
                {
                    Value = encryptionKey.KeyArn,
                    Description = "Configure in Kiro Console > Settings > Encryption key",
                });
            }

            // Raw bucket: Kiro writes User Activity Report CSVs here via the KiroWrite policy.
            var rawBucket = CreateProtectedBucket("KiroUsageRawBucket", "kiro-usage-raw", encryptionKey);
            AddKiroWritePolicy(rawBucket);

            // Analytics bucket: curated Parquet under usage_daily/ + model_messages/ and
            // disposable Athena output under athena-results/ (short-expiry lifecycle).
            var analyticsBucket = CreateProtectedBucket("KiroUsageAnalyticsBucket", "kiro-usage-analytics", encryptionKey,
            [
                new LifecycleRule
                {
                    Id = "ExpireAthenaResults",
                    Enabled = true,
                    Prefix = "athena-results/",
                    Expiration = Duration.Days(AthenaResultsExpiryDays),
                },
            ]);

            // Target List: the allowlist of User Emails the pipeline keeps (fail-closed).
            // Plain StringList (not SecureString) — emails aren't secret and it's editable
            // without a redeploy.
            var targetList = new StringListParameter(this, "TargetList", new StringListParameterProps
            {
                ParameterName = "/kiro-usage/target-list",
                Description = "Target List: User Emails the pipeline retains (fail-closed allowlist)",
                Tier = ParameterTier.STANDARD,
                StringListValue = ["dwalleck@proton.me"],
            });

            new CfnOutput(this, "RawBucketUri", new CfnOutputProps
            {
                Value = $"s3://{rawBucket.BucketName}/",
                Description = "Kiro Console > Settings > User activity report > S3 location",
            });

            new CfnOutput(this, "AnalyticsBucketName", new CfnOutputProps
            {
                Value = analyticsBucket.BucketName,
                Description = "Analytics bucket holding curated Parquet + athena-results/",
            });

            new CfnOutput(this, "TargetListParameterName", new CfnOutputProps
            {
                Value = targetList.ParameterName,
                Description = "SSM StringList parameter holding the Target List of User Emails",
            });
        }

        // Common protected-bucket shape: SSE-S3 (or CMK via the UseCustomKey toggle),
        // Block Public Access, EnforceSSL, and RETAIN on delete.
        private Bucket CreateProtectedBucket(string id, string nameSuffix, Key encryptionKey, ILifecycleRule[] lifecycleRules = null)
        {
            return new Bucket(this, id, new BucketProps
            {
                BucketName = $"{nameSuffix}-{Account}-{Region}",
                Encryption = encryptionKey != null ? BucketEncryption.KMS : BucketEncryption.S3_MANAGED,
                EncryptionKey = encryptionKey,
                BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
                EnforceSSL = true,
                RemovalPolicy = RemovalPolicy.RETAIN,
                LifecycleRules = lifecycleRules,
            });
        }

        // Allow Kiro (q.amazonaws.com) to PutObject, scoped to this account and the
        // codewhisperer service ARN. Only the raw bucket receives Kiro writes.
        private void AddKiroWritePolicy(Bucket bucket)
        {
            bucket.AddToResourcePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Sid = "KiroWrite",
                Effect = Effect.ALLOW,
                Principals = [new ServicePrincipal(KiroServicePrincipal)],
                Actions = ["s3:PutObject"],
                Resources = [bucket.ArnForObjects("*")],
                Conditions = KiroSourceConditions(),
            }));
        }

        // Confused-deputy guard shared by the KiroWrite bucket policy and the CMK key
        // policy: pin the Kiro service principal to this account + the codewhisperer
        // service ARN so only our own Kiro delivery can assume it.
        private Dictionary<string, object> KiroSourceConditions()
        {
            return new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, string>
                {
                    ["aws:SourceAccount"] = Account,
                },
                ["ArnLike"] = new Dictionary<string, string>
                {
                    ["aws:SourceArn"] = $"arn:aws:codewhisperer:{Region}:{Account}:*",
                },
            };
        }
    }
}
