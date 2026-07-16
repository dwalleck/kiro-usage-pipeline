# 14 — Grafana automation integration spike

**Type:** task

**Status:** needs-triage

**What to build:** A temporary, isolated Amazon Managed Grafana workspace that proves the
production-equivalent automation path without modifying the deployed `KiroInfraStack` workspace.
It must use the existing IAM Identity Center instance in `us-east-2`, a temporary workspace in
`us-east-1`, and the existing Athena/Glue/S3 data plane. It must remain deployed for review until
explicit cleanup approval.

**Blocked by:** Standard Grafana Viewer authorization permits arbitrary data-source queries; the production access model needs a maintainer decision.

## Decisions already made

- IAM Identity Center is locally managed for this prototype.
- CDK creates and retains three IAM Identity Center groups in `us-east-2`; it never manages
  individual memberships:
  - `kiro-usage-grafana-admins`
  - `kiro-usage-grafana-editors`
  - `kiro-usage-grafana-viewers`
- The demo user is a **Viewer**. Viewer access is authorized for the data exposed by the deployed
  dashboards.
- Viewers are intended to be dashboard-only: they must not edit dashboards or gain ad hoc
  Explore/Athena-query access. The spike must verify the actual Amazon Managed Grafana behavior.
- Dashboard JSON in the repository is the source of truth. A subsequent deployment overwrites UI
  drift rather than preserving it.
- The spike uses the actual `AWS_SSO` authentication path, including the `us-east-2` IAM Identity
  Center instance and `us-east-1` Grafana workspace.
- The temporary workspace and its review artifacts must **not** be deleted automatically. Cleanup
  requires explicit approval after manual review.

## Work completed so far

- [x] Inspected the CDK app, Managed Grafana construct, dashboard assets, deployment conventions,
      existing Lambda packaging, test conventions, and live-stack state.
- [x] Verified that no Managed Grafana workspace currently exists in the deployed account.
- [x] Verified the IAM Identity Center instance is active in `us-east-2` with identity store
      `d-9a673a2cf5`.
- [x] Added `src/KiroInfra/IdentityFoundationStack.cs`.
      - Creates the three dedicated groups with `AWS::IdentityStore::Group`.
      - Uses `RemovalPolicy.RETAIN`.
      - Outputs each `GroupId`.
      - Uses generic `CfnResource`, because pinned CDK `2.259.0` has no generated Identity Store
        L1 binding even though CloudFormation supports the resource.
- [x] Added `src/KiroInfra/GrafanaIntegrationSpikeStack.cs`.
      - Isolated `us-east-1` stack with distinct temporary workspace name.
      - Accepts analytics-bucket and all group IDs as explicit parameters.
      - Tags resources as temporary and requiring explicit cleanup approval.
- [x] Added `src/KiroInfra/GrafanaProvisioning.cs`.
      - Packages canonical dashboard JSON as CDK S3 assets.
      - Adds the custom-resource provider Lambda and scoped IAM permissions.
      - Passes workspace, group, Athena, and asset metadata to the provider.
- [x] Added `src/KiroGrafanaProvisioner/` with a .NET 10 Lambda provider.
      - Assigns Admin, Editor, and Viewer roles through the Managed Grafana control-plane API.
      - Creates an Admin service account and a short-lived token only for the invocation.
      - Creates or reconciles the `Kiro Usage` folder, Athena data source, and both dashboards.
      - Deletes the token and service account in `finally`; no provisioning credential is retained.
      - Reports CloudFormation custom-resource success/failure to `ResponseURL`.
- [x] Added stable dashboard UIDs:
      - `kiro-usage-fleet-overview`
      - `kiro-usage-user-drilldown`
- [x] Updated `src/KiroInfra/Program.cs` to synthesize all three stacks.
- [x] `dotnet build src/KiroInfra.sln` succeeds after correcting CDK binding syntax.

## Remaining tasks

### 1. Resolve and record the NuGet restore issue

- [x] Retry the provider restore when the feed is healthy:

  ```bash
  dotnet restore src/KiroGrafanaProvisioner/KiroGrafanaProvisioner.csproj \
    --disable-parallel --verbosity minimal
  ```

- [x] Confirm the exact pinned packages restore successfully:
  - `AWSSDK.ManagedGrafana` `4.0.100.4`
  - `AWSSDK.S3` `4.0.101.1`
  - `Amazon.Lambda.Core` `3.1.1`
  - `Amazon.Lambda.RuntimeSupport` `2.1.2`
  - `Amazon.Lambda.Serialization.SystemTextJson` `3.0.0`
- [x] If restore still fails after the feed incident, capture the full restore output and decide
      whether the issue is network/TLS/proxy/cache related before changing dependencies.
- [x] Do not replace the provider with AWS CLI calls or embed a long-lived Grafana API token as a
      workaround.

**Acceptance:** restore completes with explicit latest-stable package pins and no unpinned packages.

### 2. Compile and correct the Grafana provider against the resolved SDK

- [x] Build the provider:

  ```bash
  dotnet build src/KiroGrafanaProvisioner/KiroGrafanaProvisioner.csproj
  ```

- [x] Correct any AWS SDK model/API-name mismatches discovered by compilation, especially:
  - Managed Grafana client, request, and response model types;
  - `UpdatePermissions` group role assignments;
  - service-account and service-account-token creation/deletion requests;
  - explicit `SSO_GROUP` principal type;
  - custom-resource Lambda handler compatibility with the .NET 10 runtime.
- [x] Preserve the security invariants while correcting API syntax:
  - service-account token lifetime is no more than 15 minutes;
  - token and service account are deleted after every create/update invocation;
  - cleanup failure fails the CloudFormation operation rather than silently retaining a credential;
  - provider can read only the two CDK dashboard assets and operate only on its workspace.

**Acceptance:** provider compiles with zero warnings and no long-lived provisioning credential is
introduced.

### 3. Verify CDK synthesis and the generated CloudFormation contracts

- [x] Run a full synth after the provider builds:

  ```bash
  npx cdk synth --profile AdministratorAccess-369434902231 --strict
  ```

- [x] Inspect `KiroIdentityFoundationStack` output:
  - pinned to `us-east-2`;
  - three `AWS::IdentityStore::Group` resources;
  - `DeletionPolicy: Retain` and `UpdateReplacePolicy: Retain`;
  - `GroupId` outputs use `Fn::GetAtt`.
- [x] Inspect `KiroGrafanaIntegrationSpikeStack` output:
  - target region remains `us-east-1`;
  - workspace uses `AWS_SSO` and `CUSTOMER_MANAGED` permissions;
  - workspace name is `Kiro-Usage-Integration-Spike`, never `Kiro-Usage`;
  - custom resource depends on the workspace;
  - CDK assets are passed as bucket/key properties;
  - the provider role contains only required Grafana API actions and scoped asset reads.
- [x] Use the AWS IaC MCP validation tool against the synthesized template if it is available;
      otherwise run the repository-supported CDK synthesis and record the MCP limitation.

**Acceptance:** all stacks synthesize; the generated templates match the regional, naming,
retention, dependency, and least-privilege constraints above.

### 4. Validate provider behavior without deploying the permanent workspace

- [x] Review the custom-resource handler for CloudFormation lifecycle behavior:
  - Create and Update perform reconciliation.
  - Delete performs no destructive Grafana API operation because the temporary workspace itself is
    CloudFormation-owned and credentials are already removed.
  - Every code path reports a response to CloudFormation's `ResponseURL`.
- [x] Verify idempotent behavior by inspection:
  - folder conflict is resolved by reading the existing folder;
  - data source uses its stable UID `kiro-athena` and is updated in place;
  - dashboard UIDs are stable and `overwrite: true` is set;
  - permission responses are checked and unexpected errors fail the deployment.
- [ ] After temporary deployment, verify idempotency by deploying the spike stack twice.
- [x] Confirm the Athena data-source configuration uses:
  - `AwsDataCatalog`;
  - database `kiro_usage`;
  - workgroup `kiro-usage`;
  - `us-east-1`;
  - `s3://<analytics-bucket>/athena-results/`;
  - workspace IAM role authentication.
- [x] Confirm the provider's health-check failure surfaces as a failed custom resource rather
      than a successful but unusable workspace.

**Acceptance:** a second stack deployment reconciles existing resources, does not create duplicate
dashboards, and surfaces any Athena/Grafana configuration failure.

### 5. Update operational documentation

- [x] Update `README.md` with a dedicated **temporary Grafana integration spike** workflow.
- [x] Update `.scratch/kiro-usage-dashboard/dashboards/README.md` to distinguish the automated
      temporary-spike path from the existing manual production-workspace instructions.
- [x] Document the required manual IAM Identity Center membership actions after the foundation
      stack deploys:
  - add the primary operator to `kiro-usage-grafana-admins`;
  - add the demo person to `kiro-usage-grafana-viewers`;
  - leave the Editors group empty unless intentional exploratory UI editing is required.
- [x] Document the ordered deployment commands, with values obtained from CloudFormation outputs:

  ```bash
  # Identity Center home Region
  npx cdk deploy KiroIdentityFoundationStack \
    --profile "$AWS_PROFILE" \
    --region us-east-2 \
    --strict

  # Temporary Grafana integration spike in the data-plane Region
  npx cdk deploy KiroGrafanaIntegrationSpikeStack \
    --profile "$AWS_PROFILE" \
    --region us-east-1 \
    --strict \
    --parameters AnalyticsBucketName=<KiroInfraStack AnalyticsBucketName> \
    --parameters GrafanaAdminGroupId=<foundation output> \
    --parameters GrafanaEditorGroupId=<foundation output> \
    --parameters GrafanaViewerGroupId=<foundation output>
  ```

- [x] State prominently that these commands create AWS resources and incur Managed Grafana cost;
      they require explicit approval and are not part of this code-only implementation.
- [x] Document cleanup as a separate, explicit approval step. Do not document or automate an
      immediate `cdk destroy` action.

**Acceptance:** an operator can perform the reviewed deployment and membership steps without
using the Grafana UI to configure the data source, folder, or dashboards.

### 6. Deploy and conduct the approved manual integration review

> Deployment was explicitly approved on 2026-07-16. The temporary workspace remains deployed for
> review; cleanup still requires separate explicit approval.

- [x] Deploy the foundation stack in `us-east-2`.
- [x] Manually add group members in IAM Identity Center.
- [x] Deploy the temporary spike stack in `us-east-1`.
- [x] Verify through an Admin service account:
  - workspace is reachable through the stack output URL;
  - Athena data source passes its health check;
  - both dashboards are in `Kiro Usage`;
  - an Athena query through the provisioned data source returns existing facts.
- [ ] Complete the human IAM Identity Center Admin UI review.
- [x] Probe the standard Viewer role:
  - both dashboard API reads return HTTP 200;
  - dashboard update returns HTTP 403;
  - an arbitrary `SELECT count(*) FROM usage_daily` request to `/api/ds/query` returns HTTP 200
    and data. This fails the dashboard-only access invariant, so reconciliation testing stopped.
- [ ] Make a harmless dashboard UI edit as an Admin, deploy the spike stack a second time, and
      verify the committed JSON restores the expected definition without duplication. Do not run
      until the Viewer access-control decision is resolved.
- [x] Inspect the Managed Grafana workspace and CloudWatch logs to verify no service account or
      service-account token remains after provisioning and the authorization probes.
- [x] Leave the temporary workspace deployed for review. Request explicit approval before cleanup.

**Acceptance:** all security, role, data-source, dashboard, idempotency, and credential-lifecycle
checks pass; otherwise record the exact failed condition and revise the implementation before any
permanent stack change.

### 7. Final review and handoff

- [x] Run `git diff --check` and inspect the full diff.
- [x] Run all available build/synthesis checks after the NuGet incident clears.
- [x] Update this issue's status and append exact outcomes under `## Comments`.
- [x] Do not modify or redeploy `KiroInfraStack`'s production-named workspace until the temporary
      spike has passed review.

**Acceptance:** source changes, validation evidence, deployment boundary, and cleanup boundary are
clear enough for a different operator to continue safely.

## Current validation record

- `dotnet build src/KiroInfra.sln` — passed after replacing the unavailable generated Identity
  Store L1 binding with the documented generic `CfnResource` implementation.
- `DOTNET_SYSTEM_NET_DISABLEIPV6=1 dotnet restore
  src/KiroGrafanaProvisioner/KiroGrafanaProvisioner.csproj --disable-parallel --verbosity minimal`
  — passed. The stall was an unusable IPv6 route to `api.nuget.org`: IPv6 timed out, IPv4 returned
  HTTP 200, and a cold-cache restore completed in 1.11 seconds when IPv6 was disabled for the
  `dotnet` process. All five direct package pins are present in `obj/project.assets.json`.
- `dotnet build src/KiroGrafanaProvisioner/KiroGrafanaProvisioner.csproj` — passed with zero
  warnings and zero errors after aligning the provider entry point, SDK v4 behavior, and typed
  Managed Grafana constants.
- `npx cdk synth --profile AdministratorAccess-369434902231 --strict` — synthesized all three
  stacks. Contract assertions confirmed the two pinned Regions, three retained Identity Store
  groups and `GroupId` outputs, isolated workspace name/authentication, implicit workspace
  dependency, asset properties, five workspace-scoped Grafana actions, and exactly two
  object-scoped S3 reads.
- AWS IaC MCP `cfn-lint` validation — identity template: zero errors and zero warnings; Grafana
  template: zero errors and one CDK-generated W3005 warning for the Lambda function's redundant
  service-role dependency. The dependency is generated by the Lambda L2 alongside its default
  policy and is not authored by this spike.
- `dotnet package list --project src/KiroGrafanaProvisioner/KiroGrafanaProvisioner.csproj
  --outdated --format json` — reported no outdated direct packages after upgrading `AWSSDK.S3`
  from `4.0.100.3` to latest stable `4.0.101.1`; the other four pins were already latest.
- ILSpy inspection of the exact `AWSSDK.ManagedGrafana` `4.0.100.4` binary confirmed SDK v4
  response collections can be null, role/action properties have typed constants, and Identity
  Center groups require `UserType.SSO_GROUP`. The provider now handles null permission errors and
  uses `Role`, `UpdateAction`, and `UserType` constants.
- Grafana's documented data-source health response includes a JSON `status`; the provider now
  requires `status: OK`, so HTTP 200 with an unhealthy status fails the custom resource.
- `dotnet build src/KiroIngest/KiroIngest.csproj` — passed with zero warnings and zero errors.
- `dotnet run --project test/KiroIngest.Tests/KiroIngest.Tests.csproj` — 78 passed, zero failed,
  zero skipped.
- `git diff --check` — passed.
- The approved foundation and temporary Grafana stacks were deployed on 2026-07-16. The temporary
  workspace is retained for review; the deployed `KiroInfraStack` was not modified.

## Comments

- The direct AWS IaC MCP documentation reader validated the `AWS::IdentityStore::Group` resource
  contract. Its search endpoints currently return an MCP-internal `url` error, so synthesis and
  direct official-resource documentation remain the reliable validation route for this session.

- Deployment review found and corrected four provider/infrastructure defects before the successful
  deployment: the service role now uses the real managed Athena policy ARN, the custom resource can
  invoke its provider Lambda, IAM Identity Center profile association permissions are present, and
  the Athena role can validate and write its result bucket.
- Admin API smoke checks returned HTTP 200 for workspace health, both dashboards, and an Athena
  query. The query returned the two existing `usage_daily` rows.
- A temporary Viewer service account could read both dashboards and could not update them, but its
  arbitrary `/api/ds/query` request returned HTTP 200 and queried the Athena data source. Grafana's
  data-source management documentation confirms that this is the default Viewer behavior; data
  source query permissions require Grafana Enterprise or Grafana Cloud:
  https://grafana.com/docs/grafana/latest/administration/data-source-management/#data-source-permissions
  Those permissions gate all data-source queries; granting the query permission needed by dashboard
  panels would still permit arbitrary queries, so they do not enforce dashboard-only access.
- All short-lived Admin and Viewer service-account tokens and accounts were deleted. The workspace
  currently reports zero service accounts, and its provider log group contains no cleanup errors.
- Human SSO UI review and the second-deploy reconciliation check remain intentionally open. The
  Viewer authorization failure must be resolved before this pattern changes the permanent stack.
