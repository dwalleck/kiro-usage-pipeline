using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Assets;
using Constructs;
using System.Collections.Generic;
using AssetOptions = Amazon.CDK.AWS.S3.Assets.AssetOptions;

namespace KiroInfra
{
    // CloudFormation cannot model Grafana folders, data sources, dashboards, or Identity
    // Center workspace-role assignments. This construct packages the canonical dashboard
    // JSON as S3 assets and invokes a short-lived-token provider to reconcile them.
    public sealed class GrafanaProvisioning : Construct
    {
        public const string FleetDashboardUid = "kiro-usage-fleet-overview";
        public const string UserDrilldownDashboardUid = "kiro-usage-user-drilldown";
        private const string ProvisionerProjectPath = "src/KiroGrafanaProvisioner";
        private static readonly Runtime DotNet10 = new("dotnet10");

        public Function ProviderFunction { get; }
        public CfnCustomResource Resource { get; }

        public GrafanaProvisioning(
            Construct scope,
            string id,
            CfnResource workspace,
            IBucket analyticsBucket,
            string adminGroupId,
            string editorGroupId,
            string viewerGroupId,
            string databaseName,
            string workGroupName) : base(scope, id)
        {
            var fleetDashboard = new Asset(this, "FleetDashboardAsset", new AssetProps
            {
                Path = ".scratch/kiro-usage-dashboard/dashboards/a-fleet-overview.json",
            });
            var userDrilldownDashboard = new Asset(this, "UserDrilldownDashboardAsset", new AssetProps
            {
                Path = ".scratch/kiro-usage-dashboard/dashboards/b-user-drilldown.json",
            });

            ProviderFunction = new Function(this, "ProviderFunction", new FunctionProps
            {
                Runtime = DotNet10,
                Architecture = Architecture.X86_64,
                Handler = "KiroGrafanaProvisioner",
                MemorySize = 512,
                Timeout = Duration.Minutes(10),
                Description = "Reconciles temporary Kiro Grafana workspace roles, Athena source, and dashboards",
                Code = Code.FromAsset(ProvisionerProjectPath, new AssetOptions
                {
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
            });

            // The provider can operate only on this workspace and fetch exactly the two
            // immutable dashboard objects. Lambda's managed policy supplies log writes.
            var workspaceArn = Stack.Of(this).FormatArn(new ArnComponents
            {
                Service = "grafana",
                Resource = "/workspaces",
                ResourceName = workspace.Ref,
            });
            ProviderFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions =
                [
                    "grafana:UpdatePermissions",
                    "grafana:CreateWorkspaceServiceAccount",
                    "grafana:CreateWorkspaceServiceAccountToken",
                    "grafana:DeleteWorkspaceServiceAccountToken",
                    "grafana:DeleteWorkspaceServiceAccount",
                ],
                Resources = [workspaceArn],
            }));

            // UpdatePermissions validates IAM Identity Center principals through these
            // globally-scoped APIs; AWS does not define resource-level ARNs for them.
            ProviderFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions =
                [
                    "sso:DescribeRegisteredRegions",
                    "sso:GetSharedSsoConfiguration",
                    "sso:ListDirectoryAssociations",
                    "sso:GetManagedApplicationInstance",
                    "sso:ListProfiles",
                    "sso:AssociateProfile",
                    "sso:DisassociateProfile",
                    "sso:GetProfile",
                    "sso:ListProfileAssociations",
                    "sso-directory:DescribeUser",
                    "sso-directory:DescribeGroup",
                ],
                Resources = ["*"],
            }));
            ProviderFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
            {
                Effect = Effect.ALLOW,
                Actions = ["s3:GetObject"],
                Resources =
                [
                    $"arn:{Aws.PARTITION}:s3:::{fleetDashboard.S3BucketName}/{fleetDashboard.S3ObjectKey}",
                    $"arn:{Aws.PARTITION}:s3:::{userDrilldownDashboard.S3BucketName}/{userDrilldownDashboard.S3ObjectKey}",
                ],
            }));

            Resource = new CfnCustomResource(this, "Resource", new CfnCustomResourceProps
            {
                ServiceToken = ProviderFunction.FunctionArn,
                ServiceTimeout = 630,
            });
            Resource.AddPropertyOverride("WorkspaceId", workspace.Ref);
            Resource.AddPropertyOverride("WorkspaceEndpoint", Fn.GetAtt(workspace.LogicalId, "Endpoint"));
            Resource.AddPropertyOverride("AdminGroupId", adminGroupId);
            Resource.AddPropertyOverride("EditorGroupId", editorGroupId);
            Resource.AddPropertyOverride("ViewerGroupId", viewerGroupId);
            Resource.AddPropertyOverride("AnalyticsBucketName", analyticsBucket.BucketName);
            Resource.AddPropertyOverride("DatabaseName", databaseName);
            Resource.AddPropertyOverride("WorkGroupName", workGroupName);
            Resource.AddPropertyOverride("Region", Stack.Of(this).Region);
            Resource.AddPropertyOverride("FleetDashboardAssetBucket", fleetDashboard.S3BucketName);
            Resource.AddPropertyOverride("FleetDashboardAssetKey", fleetDashboard.S3ObjectKey);
            Resource.AddPropertyOverride("UserDrilldownDashboardAssetBucket", userDrilldownDashboard.S3BucketName);
            Resource.AddPropertyOverride("UserDrilldownDashboardAssetKey", userDrilldownDashboard.S3ObjectKey);
            // The provider asserts each committed dashboard's uid against these before
            // upserting, so the expected UIDs live in exactly one place: this construct.
            Resource.AddPropertyOverride("FleetDashboardUid", FleetDashboardUid);
            Resource.AddPropertyOverride("UserDrilldownDashboardUid", UserDrilldownDashboardUid);

            var invocationPermission = new CfnPermission(this, "AllowCloudFormationInvocation", new CfnPermissionProps
            {
                Action = "lambda:InvokeFunction",
                FunctionName = ProviderFunction.FunctionName,
                Principal = "cloudformation.amazonaws.com",
                SourceAccount = Stack.Of(this).Account,
            });
            Resource.AddDependency(invocationPermission);
        }
    }
}
