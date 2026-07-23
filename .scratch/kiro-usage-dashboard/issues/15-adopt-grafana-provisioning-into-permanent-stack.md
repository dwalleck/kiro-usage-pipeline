# 15 — Adopt Grafana provisioning into the permanent stack

**Type:** task

**Status:** needs-triage

**What to build:** Move the proven Grafana automation path out of the temporary
integration spike and into `KiroInfraStack`, so deploying the permanent stack reconciles the
`Kiro-Usage` workspace's Athena data source, `Kiro Usage` folder, and both committed
dashboards — no manual console steps and no ad-hoc API scripting.

**Blocked by:** The unresolved Viewer access-model decision from
[14 — Grafana automation integration spike](14-grafana-automation-integration-spike.md):
Grafana OSS Viewers can issue arbitrary `/api/ds/query` requests (data-source query
permissions are Grafana Enterprise/Cloud only), so the dashboard-only Viewer invariant does
not hold. Single-user Admin operation is unaffected; the decision matters before any Viewer
is assigned to the permanent workspace.

## Background

- The spike proved the pattern end-to-end on 2026-07-16/17: a Lambda-backed CloudFormation
  custom resource that assigns Identity Center group roles, then reconciles folder, Athena
  data source, and dashboards through a short-lived service-account token (≤ 15 min) that is
  deleted in `finally`.
- On 2026-07-22 the permanent `Kiro-Usage` workspace was provisioned by hand with one-off
  shell scripts (service account + token + raw `curl`). It worked, but it is not
  reproducible, left no credential hygiene guarantees encoded in infrastructure, and
  required four rounds of debugging that the provisioner already handles (folder 409/412
  reconciliation, plugin install, health-check polling, dashboard UID assertions).
- The provisioner code is committed and deployed-tested:
  `src/KiroInfra/GrafanaProvisioning.cs` (construct) and `src/KiroGrafanaProvisioner/`
  (.NET 10 Lambda provider).

## Scope

- Instantiate `GrafanaProvisioning` in `KiroInfraStack`, wired to the permanent
  `GrafanaWorkspace` construct's workspace and data-source role, with the three Identity
  Center group IDs from `KiroIdentityFoundationStack` passed as parameters (same pattern as
  the spike stack).
- Dashboard JSON remains the source of truth in
  `.scratch/kiro-usage-dashboard/dashboards/`; deployments overwrite UI drift
  (`overwrite: true`, stable UIDs).
- Update `README.md` and `.scratch/kiro-usage-dashboard/dashboards/README.md`: the
  "manual production-workspace import" path becomes automated.

## Constraints carried over from the spike

- Token lifetime ≤ 15 minutes; token and service account deleted after every invocation;
  cleanup failure **fails** the CloudFormation operation.
- Provider role is workspace-scoped (five Grafana actions) and reads exactly the two CDK
  dashboard assets; Identity Center validation actions stay globally scoped (AWS defines no
  resource ARNs for them).
- Permanent workspace name is `Kiro-Usage` — never create or reuse a temporary-named
  workspace.
- No long-lived Grafana API token may be introduced anywhere.

## Acceptance

- `npx cdk deploy KiroInfraStack` reconciles the workspace end-to-end: folder, data source
  health-check `OK`, both dashboards present in `Kiro Usage` with live data.
- A second deploy (after a deliberate UI edit) restores the committed dashboard definitions
  without duplicates.
- Workspace reports zero service accounts after provisioning completes.
- The Viewer access-model decision is recorded in this issue before any Viewer assignment.

## Comments

- 2026-07-22 session evidence for why manual provisioning is not good enough: the one-off
  script path needed four debug rounds, including a silent `jq --slurpfile`/`stdin` trap
  that POSTed empty dashboard bodies (Grafana correctly returned
  `400 bad request data` — the fix was `jq -n`). The provisioner's typed client and payload
  assertions prevent this class of failure.
- Same session: `KiroInfraStack` first-ever deploy required two fixes before provisioning
  could run (self-invoke IAM cycle, missing `sqs:SendMessage` for the EventInvokeConfig
  DLQ destination) — another data point that untested manual paths drift from the code.
