using Amazon.CDK;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace KiroInfra
{
    // A deliberately isolated workspace used to prove the end-to-end automation path.
    // It reads the existing Athena/Glue/S3 data plane but creates no persistent dashboards
    // or workspace in the production-named KiroInfraStack.
    public sealed class GrafanaIntegrationSpikeStack : Stack
    {
        internal GrafanaIntegrationSpikeStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            Amazon.CDK.Tags.Of(this).Add("Purpose", "temporary-grafana-integration-spike");
            Amazon.CDK.Tags.Of(this).Add("Cleanup", "requires-explicit-approval");

            var analyticsBucketName = new CfnParameter(this, "AnalyticsBucketName", new CfnParameterProps
            {
                Type = "String",
                Description = "Existing Kiro analytics bucket from KiroInfraStack.AnalyticsBucketName",
            });
            var adminGroupId = GroupParameter("GrafanaAdminGroupId", "Identity Center group ID granted Grafana Admin");
            var editorGroupId = GroupParameter("GrafanaEditorGroupId", "Identity Center group ID granted Grafana Editor");
            var viewerGroupId = GroupParameter("GrafanaViewerGroupId", "Identity Center group ID granted Grafana Viewer");

            var analyticsBucket = Bucket.FromBucketName(this, "ExistingAnalyticsBucket", analyticsBucketName.ValueAsString);
            var grafana = new GrafanaWorkspace(
                this,
                "TemporaryGrafanaWorkspace",
                analyticsBucket,
                QueryLayer.WorkGroupName,
                workspaceName: "Kiro-Usage-Integration-Spike",
                workspaceDescription: "Temporary workspace for Kiro usage Grafana automation validation");

            _ = new GrafanaProvisioning(
                this,
                "GrafanaProvisioning",
                grafana.Workspace,
                analyticsBucket,
                adminGroupId.ValueAsString,
                editorGroupId.ValueAsString,
                viewerGroupId.ValueAsString,
                QueryLayer.DatabaseName,
                QueryLayer.WorkGroupName);

            new CfnOutput(this, "GrafanaWorkspaceUrl", new CfnOutputProps
            {
                Value = $"https://{grafana.Workspace.AttrEndpoint}",
                Description = "Temporary integration-spike workspace URL; delete only after explicit approval",
            });
            new CfnOutput(this, "FleetOverviewUrl", new CfnOutputProps
            {
                Value = $"https://{grafana.Workspace.AttrEndpoint}/d/{GrafanaProvisioning.FleetDashboardUid}",
                Description = "Automated fleet overview dashboard",
            });
            new CfnOutput(this, "UserDrilldownUrl", new CfnOutputProps
            {
                Value = $"https://{grafana.Workspace.AttrEndpoint}/d/{GrafanaProvisioning.UserDrilldownDashboardUid}",
                Description = "Automated user drilldown dashboard",
            });
        }

        private CfnParameter GroupParameter(string id, string description)
        {
            return new CfnParameter(this, id, new CfnParameterProps
            {
                Type = "String",
                Description = description,
            });
        }
    }
}
