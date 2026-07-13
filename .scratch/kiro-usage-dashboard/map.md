# Kiro Usage Dashboard POC — Wayfinder Map

Label: `wayfinder:map`

## Destination

A **spec** for a POC pipeline that reads Kiro's **User Activity Report** CSVs from the
existing source buckets, filters them to a **Target List** of **User Emails**, unpivots and
lands them as query-ready Parquet, and exposes them for an **Amazon Managed Grafana**
dashboard over Athena. The spec is grounded by early AFK inspection of the *real* data and
is precise enough to hand to an implementer. The map does **not** build the pipeline — it
stops at a hand-off-able spec (plus the grounding facts the spec needs).

## Notes

- **Domain**: see `CONTEXT.md` (glossary) at repo root. Use its terms verbatim
  (User Activity Report, Target List, Target User, Daily Usage Fact, Model Message Fact,
  Unpivot). Metrics reference: `per-user-activity.txt`, `prompt-logging.txt`.
- **Skills**: resolve tickets with `/grilling` + `/domain-modeling`; `/prototype` for the
  Grafana design ticket; `/research` for the Parquet.Net spike.
- **Region topology** (updated by ticket 05): the **entire POC collapses into `us-east-1`** —
  Lambda, target/analytics bucket, Glue/Athena, and Grafana all co-locate with the **source**
  User Activity Report bucket (which Kiro pins to `us-east-1`, the profile region). This
  supersedes the earlier `us-east-2` choice: no cross-region read, no cross-region event
  wiring. Profile `AdministratorAccess-369434902231`. Single-account (no cross-account IAM).
- **Language**: C# throughout — C# CDK (as in `src/KiroInfra`) + a C# Lambda using
  **Parquet.Net** (pure-managed Parquet library).
- **This is a planning map**: produce decisions and a spec, not a running pipeline. The one
  exception is the AFK *task* tickets that inspect real data to ground the spec.

## Decisions so far

<!-- one line per closed ticket; zoom the link for detail -->

- [01 — Inspect the real User Activity Report data](issues/01-inspect-real-activity-report-data.md)
  — Reports live in `kiro-monitoring-activity-report-369434902231` (**us-east-1**); 13 fixed
  cols + dynamic lowercase `<model>_messages`; one Target User (`dwalleck@proton.me`, `PRO_MAX`);
  no PLUGIN/part-splits. [findings](assets/01-data-inspection-findings.md)
- [02 — Spike: Parquet.Net in the .NET Lambda runtime](issues/02-parquet-net-lambda-spike.md)
  — Parquet.Net v6.0.3 (pure-managed, zero native deps) confirmed viable for
  Athena-readable Parquet from a **zip** Lambda; Snappy default; mind decimal/INT96/unsigned
  gotchas. [findings](assets/02-parquet-net-findings.md) **Runtime target: .NET 10** (managed
  Lambda runtime, GA Jan 2026; .NET 8 nears EOL — Parquet.Net lists .NET 10 as a first-class
  target, and zero native deps make the spike's zip conclusion runtime-agnostic).
- [03 — Finalize the fixed schemas for the two facts](issues/03-finalize-fact-schemas.md)
  — Frozen: lowercase snake_case cols; `date`+`client_type` are path-only partitions;
  `usage_daily` (11 body cols, credits as `double`) + `model_messages` (user_id, user_email,
  model, messages, drop zero rows); Snappy Parquet; deterministic overwrite keys; projection
  `date=2026-06-01,NOW` + `client_type` enum incl. PLUGIN.
- [04 — Decide the one-time backfill mechanism](issues/04-backfill-mechanism.md)
  — **On-demand invoke of the live Lambda**, not Batch Ops/event replay/local script.
  One polymorphic function dispatches S3 `ObjectCreated` and asynchronous `mode=backfill`
  payloads into the shared `ProcessCsv` core. Each backfill invocation handles one S3 page,
  asynchronously schedules the next continuation, and isolates object failures before
  aggregating them for retry/DLQ handling. Reserved concurrency = 1. Scope remains the full
  `user_report/` prefix filtered to `*.csv`; optional `from`/`to` bounds support targeted
  reprocessing.
- [05 — Design IAM & permissions for the pipeline](issues/05-iam-and-permissions-design.md)
  — **Collapse the whole POC into `us-east-1`** (co-located with source; reverses the
  us-east-2 note below). New CDK-managed buckets; **event-driven** S3→Lambda trigger
  (prefix+`.csv`). **Two buckets** (raw + analytics w/ `athena-results/` prefix). Target List
  = SSM `StringList` (plain). **SSE-S3** default, CMK-ready toggle documented. Lambda role:
  identity-policy read (`GetObject`+prefix `ListBucket`) / scoped `PutObject` to
  `usage_daily/`+`model_messages/` / `ssm:GetParameter` / Logs — **no KMS, no Glue** (thanks
  to projection). Dedicated Athena workgroup `kiro-usage` (enforced config, scan cap,
  results location). Grafana role = `AmazonGrafanaAthenaAccess` + scoped inline; human auth =
  **IAM Identity Center** (Okta later = SAML path).
- [06 — Design the Managed Grafana workspace and panels](issues/06-grafana-workspace-design.md)
  — Verdict via `/prototype` (iterated): **two dashboards** in a `Kiro Usage` folder sharing
  variables + data source — **A · Fleet Overview** (fleet KPI row → **cap alert: users ≥90%**,
  full per-user bars secondary → credits/messages-by-user leaderboards + tier mix → fleet trend
  plus model/client splits, aggregating all users) and **B · User Drilldown** (pick one
  `$user_email` → model-forward + detail table + that user's cap). Cap = MTD
  `sum(credits_used)` vs `overage_cap`, green/orange/red. Variables
  `$user_email`/`$client_type`(enum)/`$model` + `$__dateFilter(date)`. 13-panel catalog with
  Athena SQL frozen against ticket 03 schemas. Mock (5 fabricated users for legibility):
  [assets/06-grafana-dashboard-mock.html](assets/06-grafana-dashboard-mock.html).
- [07 — Assemble the POC spec](issues/07-assemble-poc-spec.md) — **DESTINATION REACHED.**
  All decisions (01–06) consolidated into the implementer-ready [spec.md](spec.md): scope,
  grounding facts, architecture diagram, Target List (SSM), frozen schemas + projection,
  backfill, IAM/encryption, two-dashboard Grafana design, Parquet.Net zip build + CDK changes,
  and open risks. Corrected two stale ticket phrasings against later decisions (region =
  `us-east-1`; new CDK-managed buckets, not pre-existing ones). Per steering, the **existing
  CDK is treated as replaceable sample code** (`CreateKiroBucket` kept only as a reference
  shape). Map complete.

- [08 — CDK foundation: buckets + Target List](issues/08-cdk-foundation-buckets-target-list.md)
  — **IMPLEMENTED & DEPLOYED** to `369434902231`/`us-east-1`. `KiroInfraStack.cs` rewritten:
  raw bucket `kiro-usage-raw-…` (BLOCK_ALL, EnforceSSL, RETAIN, SSE-S3, `KiroWrite` policy) +
  analytics bucket `kiro-usage-analytics-…` (same protections + 14-day `athena-results/`
  expiry) via a shared `CreateProtectedBucket` helper; Target List = plain Standard SSM
  `StringList` `/kiro-usage/target-list` seeded `dwalleck@proton.me`; `UseCustomKey` toggle
  honored on both. Next frontier: **09** (Glue + Athena) and **10** (ingest Lambda), both
  unblocked and parallel-able.

- [09 — Glue catalog + Athena workgroup](issues/09-glue-catalog-athena-workgroup.md)
  — **IMPLEMENTED & DEPLOYED** to `369434902231`/`us-east-1`. New `QueryLayer` construct:
  Glue DB `kiro_usage`; `usage_daily` (11 body cols) + `model_messages` (4 cols) EXTERNAL
  Parquet tables, partition keys `date`+`client_type` (body-excluded), identical partition
  projection (`date` 2026-06-01,NOW / 1 DAYS; `client_type` enum KIRO_CLI,KIRO_IDE,PLUGIN;
  `storage.location.template` with `${date}/${client_type}`); Athena workgroup `kiro-usage`
  (enforced config, 1 GiB scan cap, CW metrics, `athena-results/` location). Live-verified:
  both empty tables return 0 rows in the workgroup with no schema/projection error. Next
  frontier: **10** (ingest Lambda, still unblocked); 11/12/13 unblock after 10.

## Not yet specified

<!-- in-scope fog; graduates into tickets as the frontier advances -->

- **Cross-region trigger wiring** — ✅ resolved by ticket 05: the POC collapses entirely into
  `us-east-1` (co-located with the source), so there is no cross-region hop. Trigger is a
  same-region, event-driven S3→Lambda notification.
- **Athena workgroup + query-results location** — ✅ resolved by ticket 05: dedicated
  `kiro-usage` workgroup, results at `s3://<analytics-bucket>/athena-results/`, enforced
  config + bytes-scanned cap.
- **Pipeline observability** — DLQ on the Lambda, CloudWatch alarms on failures/parse
  errors, structured logging. Revisit once the transform shape is fixed.
- **Target-bucket lifecycle / retention** — expiry, storage class, whether raw filtered
  copies are kept.
- **Lambda build & packaging** — how the C# Lambda is compiled/packaged/deployed by the
  CDK (container image vs zip, .NET version), and CI.

## Out of scope

<!-- ruled beyond the destination; closed, never graduates -->

- **Legacy Analytic Report ingestion** — has no `User_Email` and no client type, so the
  Target List can't be applied directly; needs a User Id→email bridge. Deferred past v1.
- **Prompt Log ingestion** — content keyed by Prefixed User Id, no email; out of scope for
  v1 per `CONTEXT.md`.
- **Cross-account / production delivery** — this POC is entirely within personal account
  `369434902231`. Real multi-account deployment is a later, separate effort.
- **Custom web-app dashboard** — Managed Grafana was chosen as least-effort; a bespoke web
  app is ruled out for this POC. **QuickSight** (Author Pro $40/mo) and **OSS Grafana
  self-hosted on ECS** (~$29/mo flat + ops) were also compared and ruled out for the POC on
  cost/effort at single-user scale — Managed Grafana wins at $9/mo. Full comparison +
  break-evens in [ticket 06 comments](issues/06-grafana-workspace-design.md). Revisit
  self-host only past ~4–5 viewers.
