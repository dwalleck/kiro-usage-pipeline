using Amazon.CDK;
using Constructs;

namespace KiroInfra
{
    // IAM Identity Center resources belong in its home region (us-east-2). The pinned
    // CDK library predates the generated CfnGroup binding, so this uses the documented
    // AWS::IdentityStore::Group resource directly without broadening CDK dependencies.
    public sealed class IdentityFoundationStack : Stack
    {
        public CfnResource GrafanaAdmins { get; }
        public CfnResource GrafanaEditors { get; }
        public CfnResource GrafanaViewers { get; }

        internal IdentityFoundationStack(Construct scope, string id, IStackProps props = null) : base(scope, id, props)
        {
            var identityStoreId = new CfnParameter(this, "IdentityStoreId", new CfnParameterProps
            {
                Type = "String",
                Default = "d-9a673a2cf5",
                Description = "IAM Identity Center identity store ID in us-east-2",
            });

            GrafanaAdmins = CreateGroup(
                "GrafanaAdmins",
                identityStoreId.ValueAsString,
                "kiro-usage-grafana-admins",
                "Administrators of the Kiro usage Grafana workspace");
            GrafanaEditors = CreateGroup(
                "GrafanaEditors",
                identityStoreId.ValueAsString,
                "kiro-usage-grafana-editors",
                "Editors of the Kiro usage Grafana workspace");
            GrafanaViewers = CreateGroup(
                "GrafanaViewers",
                identityStoreId.ValueAsString,
                "kiro-usage-grafana-viewers",
                "Dashboard-only viewers of the Kiro usage Grafana workspace");

            new CfnOutput(this, "GrafanaAdminGroupId", new CfnOutputProps
            {
                Value = Fn.GetAtt(GrafanaAdmins.LogicalId, "GroupId").ToString(),
                Description = "Pass to KiroGrafanaIntegrationSpikeStack as GrafanaAdminGroupId",
            });
            new CfnOutput(this, "GrafanaEditorGroupId", new CfnOutputProps
            {
                Value = Fn.GetAtt(GrafanaEditors.LogicalId, "GroupId").ToString(),
                Description = "Pass to KiroGrafanaIntegrationSpikeStack as GrafanaEditorGroupId",
            });
            new CfnOutput(this, "GrafanaViewerGroupId", new CfnOutputProps
            {
                Value = Fn.GetAtt(GrafanaViewers.LogicalId, "GroupId").ToString(),
                Description = "Pass to KiroGrafanaIntegrationSpikeStack as GrafanaViewerGroupId",
            });
        }

        private CfnResource CreateGroup(string id, string identityStoreId, string displayName, string description)
        {
            var group = new CfnResource(this, id, new CfnResourceProps
            {
                Type = "AWS::IdentityStore::Group",
            });
            group.AddPropertyOverride("IdentityStoreId", identityStoreId);
            group.AddPropertyOverride("DisplayName", displayName);
            group.AddPropertyOverride("Description", description);

            // The groups are an access boundary, not disposable application resources.
            group.ApplyRemovalPolicy(RemovalPolicy.RETAIN);
            return group;
        }
    }
}
