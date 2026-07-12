# 07 — Assemble the POC spec (destination)

Type: task
Status: resolved
Blocked by: 03, 04, 05, 06

## Question

This is the map's destination. Pull every resolved decision into a single, implementer-ready
spec at `.scratch/kiro-usage-dashboard/spec.md`.

The spec must cover:

- **Overview & scope**: v1 = User Activity Report only, in-account `369434902231`/`us-east-1`;
  Legacy Analytic Report, Prompt Log, cross-account, and custom web app explicitly out of
  scope.
- **Architecture**: source buckets → event-driven C# Lambda (filter to Target List + Unpivot,
  Parquet via Parquet.Net, deterministic overwrite keys) → target bucket → static Glue tables
  + partition projection (date + client_type) → Athena → Managed Grafana.
- **Target List**: SSM Parameter Store `StringList`, fail-closed semantics.
- **Frozen schemas** for `usage_daily` and `model_messages` (from ticket 03), including
  `User Email`.
- **Backfill** approach (ticket 04).
- **IAM/permissions & encryption** (ticket 05).
- **Grafana dashboard design** (ticket 06).
- **Build/packaging** of the C# Lambda and the CDK stack changes needed (note: the entire
  existing CDK is replaceable sample code; the stack creates new CDK-managed raw + analytics
  buckets, and Kiro is re-pointed at the new raw bucket — ticket 05).
- **Open items / risks** carried from the fog (observability, lifecycle, workgroup).

Deliverable: `spec.md` — the hand-off artifact that ends this map.


## Answer

Spec assembled at [spec.md](../spec.md) — the map's destination artifact. It consolidates
every resolved decision (tickets 01–06) into an implementer-ready document:

- **Overview & scope** — v1 = User Activity Report only, in-account `369434902231`,
  **`us-east-1`**; Legacy Analytic Report, Prompt Log, cross-account, and custom
  web-app/QuickSight/ECS-self-host all explicitly out of scope.
- **Grounding facts** (ticket 01) — bucket, key layout, 28-object volume, stray markers,
  schema, value formats, single Target User.
- **Architecture** — ASCII diagram of source → event-driven C# Lambda (filter + Unpivot +
  Parquet.Net + deterministic keys) → analytics bucket → static Glue tables + projection →
  Athena → Managed Grafana.
- **Target List** — SSM `StringList`, fail-closed (ticket 05).
- **Frozen schemas** — `usage_daily` (11 body cols) + `model_messages` (4 body cols),
  partition keys, projection config (ticket 03).
- **Backfill** — polymorphic Lambda, manual invoke, idempotent deterministic keys, sequential
  loop, `*.csv` full-prefix scan (ticket 04).
- **IAM/permissions & encryption** — bucket topology, Lambda role, trigger wiring, Athena
  workgroup, Grafana role, SSE-S3 with CMK-ready delta (ticket 05).
- **Grafana design** — two dashboards, variables, 13-panel catalog with frozen SQL (ticket 06).
- **Build/packaging** — Parquet.Net zip Lambda + the CDK stack changes.
- **Open items / risks** — observability, lifecycle, multi-user defensiveness, cap semantics,
  CMK path.

Two stale phrasings in this ticket's own body were fixed against later decisions:
**region is `us-east-1`** (was us-east-2; tickets 01/05), and the build note that **"the real
source buckets already exist"** — corrected to reflect ticket 05's decision to create new
CDK-managed raw + analytics buckets and re-point Kiro at the new raw bucket.

Per user steering during assembly, the spec's build section treats the **entire existing CDK
as replaceable sample code** (the `CreateKiroBucket` helper kept only as a reference for the
Kiro-writable bucket shape), rather than a mandated primitive to extend.

Status: resolved. This closes the map — the destination (a hand-off-able spec) is reached.
