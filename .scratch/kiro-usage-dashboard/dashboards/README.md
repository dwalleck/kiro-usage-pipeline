# Kiro Usage Dashboards

The committed JSON files are the source of truth for both dashboard paths.

## Automated temporary integration spike

`KiroGrafanaIntegrationSpikeStack` creates an isolated workspace and its custom-resource provider
automatically configures the Athena data source, creates the `Kiro Usage` folder, and reconciles
both dashboards. Do not configure those resources through the Grafana UI. Stable dashboard UIDs
and `overwrite: true` make each approved deployment restore the committed definitions.

This path creates chargeable AWS resources and requires explicit deployment approval. Follow
[Temporary Grafana integration spike](../../../README.md#temporary-grafana-integration-spike) for
the ordered Identity Center, deployment, review, and cleanup-approval workflow.

## Manual production-workspace import

The instructions below apply only to the existing production-named `Kiro-Usage` workspace path in
`KiroInfraStack`. After CDK deploys that workspace, configure Athena and import the dashboards
manually.

## Prerequisites

1. `KiroInfraStack` deployed (`npx cdk deploy KiroInfraStack --profile AdministratorAccess-369434902231 --strict`)
2. IAM Identity Center instance active (exists in us-east-2, ssoins-668452c7eadc7944)
3. A user/group assigned to the Grafana workspace in IAM Identity Center (Admin role)
4. Ingest Lambda has processed data (live path + backfill) so the facts have rows

## Setup Steps

### 1. Sign in to the Grafana workspace

Open the workspace URL from the CDK output (`GrafanaWorkspaceUrl`), sign in with IAM Identity Center.

### 2. Configure the Athena data source

Navigate to **Connections → Data sources → Add data source → Athena**.

- **Name:** `Athena`
- **Auth Provider:** Workspace IAM Role (default)
- **Data source default region:** `us-east-1`
- **Catalog:** `AwsDataCatalog`
- **Database:** `kiro_usage`
- **Workgroup:** `kiro-usage`
- **Output location:** `s3://<analytics-bucket>/athena-results/`

Click **Save & test**. You should see "Connection success."

### 3. Import the dashboards

Navigate to **Dashboards → New → Import**, then paste the JSON from:

- `.scratch/kiro-usage-dashboard/dashboards/a-fleet-overview.json` → Dashboard A · Fleet Overview
- `.scratch/kiro-usage-dashboard/dashboards/b-user-drilldown.json` → Dashboard B · User Drilldown

For each, set the folder to **Kiro Usage** (create it first: **Dashboards → New folder**).

### 4. Verify

Both dashboards should render live data from Athena for `dwalleck@proton.me`.

## Cap Semantics

The `PRO_MAX` tier cap panels use `max(overage_cap)` = 2500 (from the real data).
The cap-gauge thresholds are green <70% / orange 70–90% / red >90%.
The "Users ≥ 90% Cap" alert panel uses month-to-date aggregation (ignores the dashboard time range).
