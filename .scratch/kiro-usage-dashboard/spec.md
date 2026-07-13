# Kiro Usage Dashboard — POC Spec

Status: ready for implementation
Account: `369434902231` (personal) · Region: **`us-east-1`** · Profile: `AdministratorAccess-369434902231`
Language: C# throughout (CDK + Lambda)

This spec is the hand-off artifact for the Kiro Usage Dashboard POC. It consolidates the
resolved wayfinder decisions (tickets 01–06); each section cites its source ticket. Domain
terms are used verbatim per `CONTEXT.md` (User Activity Report, Target List, Target User,
Daily Usage Fact, Model Message Fact, Unpivot, User Id, User Email).

---

## 1. Overview & scope

Build a pipeline that reads Kiro's **User Activity Report** CSVs, filters them to a **Target
List** of **User Emails**, **Unpivots** the dynamic model columns, and lands query-ready
**Snappy Parquet** partitioned for Athena, then serves it through an **Amazon Managed
Grafana** dashboard.

**In scope (v1):**

- **User Activity Report** only, entirely within account `369434902231`, region `us-east-1`.
- Two analytics facts: **Daily Usage Fact** (`usage_daily`) and **Model Message Fact**
  (`model_messages`).
- Event-driven ingest + a one-time backfill of historical reports.
- Athena over partition-projected Glue tables; two Managed Grafana dashboards.

**Out of scope (v1):**

- **Legacy Analytic Report** (`by_user_analytic/`) — no `User_Email`, no client type, needs a
  User Id→email bridge. Deferred.
- **Prompt Log** ingestion — content keyed by Prefixed User Id, no email.
- Cross-account / production delivery — this POC is single-account, single-region.
- Custom web-app dashboard, QuickSight, and OSS-Grafana-on-ECS — Managed Grafana chosen on
  cost/effort at single-user scale (ticket 06). Revisit self-host past ~4–5 viewers.

> **Region note (supersedes stale ticket wording):** ticket 01 found the source bucket lives
> in **`us-east-1`** (the Kiro profile region), and ticket 05 collapsed the entire POC into
> `us-east-1`. Any earlier `us-east-2` reference is obsolete. Likewise ticket 03 chose
> **partition projection with no Glue crawler / no runtime Glue calls** — there are static
> Glue *table definitions* but no partition registration.

---

## 2. Grounding facts (from real data — ticket 01)

- **Source bucket:** `kiro-monitoring-activity-report-369434902231` (us-east-1). *(Note:
  ticket 05 opts to migrate onto a new CDK-managed bucket — see §9.)*
- **Key layout:**
  `<prefix>/AWSLogs/369434902231/KiroLogs/user_report/{region}/{yyyy}/{mm}/{dd}/00/{CLIENT_TYPE}_{accountId}_user_report_{yyyymmddHHMM}.csv`
- **Volume:** 28 CSVs, `2026-06-20 → 2026-07-10` (with gaps), `KIRO_CLI` + `KIRO_IDE` only
  (no `PLUGIN`), 1 data row per file, ~386–445 bytes each. No `part_1/part_2` splits.
- **Stray objects:** three 103-byte UUID marker objects directly under `KiroLogs/` — filter
  strictly to `user_report/**` + `.csv` suffix to skip them.
- **Schema:** 13 fixed static columns, then dynamic all-lowercase `<model>_messages` columns
  (9 combinations seen). `auto_messages` appears only when Auto was used — **not** a
  guaranteed column.
- **Value formats:** `Date` ISO `YYYY-MM-DD`; booleans lowercase `false`; `Subscription_Tier`
  = `PRO_MAX` (UPPER_SNAKE); `Credits_Used` full-precision float; `UserId`, `ProfileId` (full
  ARN), `User_Email` double-quoted.
- **Distinct users:** exactly 1 (`dwalleck@proton.me`, `PRO_MAX`). Single-user personal org;
  multi-user cases (part splits, `0` model cells, multiple tiers) handled defensively.

---

## 3. Architecture

```
Kiro (q.amazonaws.com)
   │  writes User Activity Report CSVs
   ▼
┌──────────────────────────┐   s3:ObjectCreated (prefix user_report/, suffix .csv)
│  Raw bucket (us-east-1)  │ ──────────────────────────────────────────────┐
│  CDK-managed             │                                                │
└──────────────────────────┘                                               ▼
                                                          ┌────────────────────────────┐
   manual one-time backfill invoke ───────────────────►   │  Ingest Lambda (C#, .NET 10)│
   {"mode":"backfill"}                                     │  Parquet.Net, zip package   │
                                                           │  ProcessCsv(bucket, key):   │
   Target List ── ssm:GetParameter ──────────────────►     │   parse → filter (Target    │
   (SSM StringList)                                        │   List) → Unpivot → write   │
                                                           └──────────────┬──────────────┘
                                                                          │ PutObject (deterministic keys)
                                                                          ▼
                                          ┌──────────────────────────────────────────────┐
                                          │  Analytics bucket (us-east-1), CDK-managed     │
                                          │   usage_daily/date=…/client_type=…/*.parquet   │
                                          │   model_messages/date=…/client_type=…/*.parquet│
                                          │   athena-results/  (short-expiry lifecycle)    │
                                          └───────────────┬───────────────┬────────────────┘
                                                          │               │
                            static Glue tables +          │               │  Athena workgroup kiro-usage
                            partition projection          ▼               ▼
                                          ┌────────────────────┐   ┌──────────────────────────┐
                                          │  Glue DB kiro_usage │   │  Amazon Managed Grafana   │
                                          │  usage_daily        │◄──│  Athena data source       │
                                          │  model_messages     │   │  A·Fleet  B·Drilldown     │
                                          └────────────────────┘   └──────────────────────────┘
```

Data flow: **source buckets → event-driven C# Lambda (filter to Target List + Unpivot,
Parquet via Parquet.Net, deterministic overwrite keys) → analytics bucket → static Glue
tables + partition projection (date + client_type) → Athena → Managed Grafana.**

---

## 4. Target List (ticket 05)

- Stored in **SSM Parameter Store**, type **`StringList`**, Standard tier, **plain** (not
  SecureString — emails aren't secret, and it's editable without a redeploy).
- The Lambda reads it via `ssm:GetParameter` on the single parameter ARN and filters rows by
  `User_Email`.
- **Fail-closed:** a user absent from the Target List is never included, even if new. This is
  a Lambda-code concern, enforced inside `ProcessCsv` (applies identically to live and
  backfill paths). For the POC the list is effectively `["dwalleck@proton.me"]`.

---

## 5. Frozen fact schemas (ticket 03)

**Conventions (both tables):**

- Column names are lowercase `snake_case`; the Lambda maps source CSV names → fact names once.
- **`date` and `client_type` are partition keys only** (Hive-style path segments), **not**
  stored in the Parquet body. Keeping `date` out of the body also sidesteps the Parquet.Net
  INT96 gotcha.
- Compression: **Snappy** (Parquet.Net default, Athena's Parquet default).
- Physical layout:

  ```
  s3://<analytics-bucket>/usage_daily/date=YYYY-MM-DD/client_type=KIRO_CLI/<key>.parquet
  s3://<analytics-bucket>/model_messages/date=YYYY-MM-DD/client_type=KIRO_CLI/<key>.parquet
  ```

- **Deterministic output key** = source CSV basename plus a short SHA-256 source-identity
  suffix, e.g. `KIRO_CLI_369434902231_user_report_202607100000-<hash>.parquet`. The hash uses
  the full source bucket/key, preventing same-basename collisions. Reprocessing reconciles
  obsolete outputs and overwrites the same current keys.

### `usage_daily` — Daily Usage Fact (grain: date, user_id, client_type)

| Column | Type |
| --- | --- |
| `user_id` | string |
| `user_email` | string |
| `chat_conversations` | bigint |
| `credits_used` | double |
| `overage_cap` | double |
| `overage_credits_used` | double |
| `overage_enabled` | boolean |
| `subscription_tier` | string |
| `total_messages` | bigint |
| `new_user` | boolean |
| `profile_id` | string |

Partition keys (path, not body): `date` (date), `client_type` (string).
Decisions: credits/overage are **`double`** (full-precision source; avoids Parquet.Net
decimal/INT96 gotchas); `profile_id` retained (constant ARN, future-proofs multi-profile).

### `model_messages` — Model Message Fact (grain: date, user_id, client_type, model)

| Column | Type |
| --- | --- |
| `user_id` | string |
| `user_email` | string |
| `model` | string |
| `messages` | bigint |

Partition keys (path, not body): `date` (date), `client_type` (string).

**Unpivot rules:**

- `model` = source column name minus the `_messages` suffix, verbatim lowercase, dots
  preserved: `claude_opus_4.8_messages` → `claude_opus_4.8`, `glm_5_messages` → `glm_5`.
- `auto_messages` → `model = "auto"`, an ordinary row (not special-cased).
- **Drop zero-count rows** — emit a row only when `messages > 0`.

Both schemas are fixed and stable across new models — a new model is a new *row* in
`model_messages`, never a new column.

### Partition projection (identical on both Glue tables)

- `date`: `projection.date.type = date`, `format = yyyy-MM-dd`, `range = 2026-06-01,NOW`,
  `interval = 1`, `interval.unit = DAYS`.
- `client_type`: `projection.client_type.type = enum`, `values = KIRO_CLI,KIRO_IDE,PLUGIN`
  (PLUGIN included though unseen).
- `storage.location.template` → each table's S3 prefix with
  `date=${date}/client_type=${client_type}`.
- Table properties: `projection.enabled = true`. No Glue crawler; the Lambda registers no
  partitions.

---

## 6. Backfill (ticket 04)

**Approach: an on-demand invoke of the *live* Ingest Lambda** — backfill is "the live
per-object path, in a loop," not a separate program.

1. **Mechanism:** on-demand Lambda invoke (not S3 Batch Ops / event re-emit / local script) —
   maximises code + environment fidelity.
2. **Structure:** one polymorphic Lambda. The handler dispatches on event shape — an S3
   `ObjectCreated` notification → process that object version; a `{"mode":"backfill",
   "from":?, "to":?, "continuationToken":?}` payload → process one bounded S3 page. Both
   branches converge on the same `ProcessCsv` core. Truncated pages schedule the next page as
   a separate asynchronous invocation.
3. **Trigger:** a **manual asynchronous CLI invoke** after `cdk deploy` (wrapped in a small
   script), so Lambda retries and the DLQ apply. It remains outside the CDK lifecycle.
4. **Idempotency:** identical deterministic-key overwrite path (§5). The key is a pure
   function of full source bucket/key plus fact partition, not wall-clock or run id. Raw-bucket
   versioning, S3 sequencer state, reserved concurrency, and stale-output reconciliation make
   live/backfill overlap converge safely.
5. **Concurrency:** reserved concurrency = 1. Each invocation processes one S3 listing page
   sequentially; a truncated page self-invokes the next continuation asynchronously. Per-object
   failures are isolated so later objects in the page are attempted, then aggregated so the page
   still receives retries and DLQ handling.
6. **Selection scope:** full-prefix scan under `user_report/`, filtered to the `.csv` suffix
   (naturally skips the stray markers). The Target List email filter stays **inside**
   `ProcessCsv` so live and backfill filter identically. Payload `from`/`to` are optional,
   default unbounded — allows reprocessing a single day after a transform fix without a code
   change.

---

## 7. IAM, permissions & encryption (ticket 05)

All statements are single-region (`us-east-1`), single-account (`369434902231`).

### 7.1 Bucket topology

- **Raw bucket** (source): CDK-managed via the existing `CreateKiroBucket` pattern; Kiro
  writes via the `q.amazonaws.com` `KiroWrite` resource policy. Data migrates off the
  pre-existing `kiro-monitoring-activity-report-369434902231` bucket and Kiro is re-pointed at
  this CDK bucket.
- **Analytics bucket** (target): curated Parquet under `usage_daily/` + `model_messages/`,
  Athena results under `athena-results/`.

### 7.2 Encryption

- **SSE-S3 default** on both buckets (`UseCustomKey=false`) → zero KMS statements anywhere.
- Design stays **CMK-ready**: the analytics bucket honors the same `UseCustomKey` toggle as
  the raw bucket. The additive KMS grants (Lambda: `kms:Decrypt` for read + `kms:GenerateDataKey`,
  `kms:Decrypt` for write; Grafana: `kms:Decrypt`, `kms:GenerateDataKey`; key-policy principals)
  are documented so flipping it on is purely additive.

### 7.3 Lambda execution role (trust `lambda.amazonaws.com`)

```
# Read raw User Activity Reports
Allow s3:GetObject
  on arn:aws:s3:::<raw-bucket>/<prefix>/AWSLogs/369434902231/KiroLogs/user_report/*
Allow s3:ListBucket
  on arn:aws:s3:::<raw-bucket>
  Condition StringLike { s3:prefix: "<prefix>/AWSLogs/369434902231/KiroLogs/user_report/*" }

# Write curated Parquet (NOT athena-results/, NOT bucket-wide)
Allow s3:PutObject
  on arn:aws:s3:::<analytics-bucket>/usage_daily/*
  on arn:aws:s3:::<analytics-bucket>/model_messages/*

# Target List
Allow ssm:GetParameter
  on arn:aws:ssm:us-east-1:369434902231:parameter/<target-list-param-name>

# Logs
Allow logs:CreateLogGroup, logs:CreateLogStream, logs:PutLogEvents
  on arn:aws:logs:us-east-1:369434902231:log-group:/aws/lambda/<fn-name>:*
```

- **No KMS** (SSE-S3). **No Glue/Athena** on the Lambda — partition projection means it never
  registers partitions. Prefix-scoped `ListBucket` and version-aware `GetObject` cover raw
  reads and backfill. Curated prefixes require read/write/delete plus scoped listing for
  reconciliation; `ingest-state/*` stores the latest S3 sequencer per source. The role can
  invoke only itself to schedule the next asynchronous backfill page and retains
  `ssm:GetParameter` on the Target List.

### 7.4 Trigger wiring

```
rawBucket.addEventNotification(
  EventType.OBJECT_CREATED,
  new LambdaDestination(fn),
  { prefix: "<prefix>/AWSLogs/369434902231/KiroLogs/user_report/", suffix: ".csv" });
```

CDK auto-adds the `s3.amazonaws.com` invoke permission scoped to the raw bucket ARN +
`aws:SourceAccount`. Same-region (both us-east-1), so the notification constraint is satisfied.

### 7.5 Athena workgroup `kiro-usage`

- `ResultConfiguration.OutputLocation = s3://<analytics-bucket>/athena-results/`,
  `EnforceWorkGroupConfiguration=true`, a modest `BytesScannedCutoffPerQuery` cap,
  `PublishCloudWatchMetricsEnabled=true`.
- Catalog `AwsDataCatalog` (Glue); database = the CDK-created Glue DB `kiro_usage`.
- Human console querying: covered by the existing `AdministratorAccess-369434902231`
  permission set — no new IAM.

### 7.6 Managed Grafana → Athena data-source role (trust `grafana.amazonaws.com`)

Attach **`AmazonGrafanaAthenaAccess`** + a scoped inline policy:

```
Allow athena:StartQueryExecution, athena:StopQueryExecution,
      athena:GetQueryExecution, athena:GetQueryResults, athena:GetWorkGroup
  on arn:aws:athena:us-east-1:369434902231:workgroup/kiro-usage
Allow glue:GetDatabase, glue:GetTable, glue:GetTables, glue:GetPartitions
  on catalog, database/kiro_usage, table/kiro_usage/usage_daily, table/kiro_usage/model_messages
Allow s3:GetObject
  on arn:aws:s3:::<analytics-bucket>/usage_daily/*
  on arn:aws:s3:::<analytics-bucket>/model_messages/*
Allow s3:ListBucket
  on arn:aws:s3:::<analytics-bucket>
  Condition StringLike { s3:prefix: ["usage_daily/*","model_messages/*","athena-results/*"] }
Allow s3:GetObject, s3:PutObject
  on arn:aws:s3:::<analytics-bucket>/athena-results/*
```

- **Human auth: IAM Identity Center** (already in use; workspace Admin assigned). Okta later =
  the SAML path (Grafana→Okta directly, or Okta→Identity Center upstream) — choosing Identity
  Center now keeps that door open.

---

## 8. Grafana dashboard design (ticket 06)

One Managed Grafana workspace (auth IAM Identity Center), one folder **`Kiro Usage`** holding
**two dashboards** sharing variables + data source. Athena data source: workgroup
`kiro-usage`, catalog `AwsDataCatalog`, database `kiro_usage`, results at
`s3://<analytics-bucket>/athena-results/`.
Mock: [assets/06-grafana-dashboard-mock.html](assets/06-grafana-dashboard-mock.html).

### Template variables

| Variable | Type | Source |
| --- | --- | --- |
| `$user_email` | query, multi | `SELECT DISTINCT user_email FROM usage_daily ORDER BY 1` |
| `$client_type` | custom enum, multi | `KIRO_CLI,KIRO_IDE,PLUGIN` (enum avoids a scan) |
| `$model` | query, multi | `SELECT DISTINCT model FROM model_messages ORDER BY 1` |
| time range | built-in | `$__dateFilter(date)` for partition pruning |

Range-scoped panels filter with `$__dateFilter(date) AND user_email IN ($user_email) AND
client_type IN ($client_type)`; parse the string `date` partition with
`date_parse(date,'%Y-%m-%d')` for time axes.

### Dashboard A · Fleet Overview (aggregates across all selected users)

1. **Fleet KPI row** (`usage_daily`) — active users `count(distinct user_email)`; fleet
   credits MTD `sum(credits_used)`; fleet messages `sum(total_messages)` (+ conversations);
   users near cap (count with utilisation ≥ 0.9).
2. **Credits by user** (bar, `usage_daily`).
3. **Messages by user** (bar, `usage_daily`).
4. **Cap proximity — leads with a "users over 90%" alert** (`usage_daily`, **month-to-date**,
   ignores dashboard time range):

   ```sql
   SELECT user_email,
          sum(credits_used) AS mtd_credits,
          max(overage_cap)  AS cap,
          sum(credits_used) / max(overage_cap) AS utilisation
   FROM usage_daily
   WHERE date >= date_format(date_trunc('month', current_date), '%Y-%m-%d')
     AND user_email IN ($user_email)
   GROUP BY user_email
   ```

   Primary: table/alert-list filtered to `utilisation >= 0.9` desc (empty ⇒ green "no users
   over 90%"). Secondary: bar-gauge of all users, thresholds **green <70% / orange 70–90% /
   red >90%**.
5. **Users by tier** (bar/pie, `usage_daily`).
6. **Fleet credits over time** (timeseries, `usage_daily`).
7. **Messages by model — fleet** (bar, `model_messages`).
8. **Client type split** (pie/bar, `usage_daily`).

### Dashboard B · User Drilldown (`$user_email` constrained to one)

- **Panel 9 — Messages/day stacked by model** (timeseries, `model_messages`).
- **Panel 10 — Model share** (bar, `model_messages`).
- **Panel 11 — Credits vs messages** (dual timeseries, `usage_daily`).
- **Panel 12 — This user's cap gauge** (panel 4 scoped to one user).
- **Panel 13 — Per-user/day detail** (table, `usage_daily` + top-model rollup).

**Cap-semantics caveat (implementer):** panels use `overage_cap` (2500 in real data) as the
ceiling. If `PRO_MAX`'s true monthly included-credit allowance differs from `overage_cap`,
swap `max(overage_cap)` for the correct allowance.

---

## 9. Build/packaging & CDK stack changes

### 9.1 Lambda build & packaging (ticket 02)

- **Parquet.Net v6.0.3** (pure-managed, zero native deps; .NET 10 is a first-class target).
- **Package as a zip .NET 10 Lambda** on the **managed .NET 10 runtime** (`Code.fromAsset` /
  `dotnet lambda package`) — no container image needed. Simpler, faster cold start. (AWS Lambda
  has shipped a managed .NET 10 runtime since Jan 2026; .NET 8 nears end of maintenance, so the
  POC targets .NET 10.)
- Use the high-level `ParquetSerializer` with plain C# DTOs for `usage_daily` and
  `model_messages`. Keep `date`/`client_type` out of the body; write one row group per output
  file to a `MemoryStream`, then `PutObject`. Default Snappy.
- Gotchas to honor: no unsigned ints (Athena rejects `UINT_64`); credits/overage as `double`
  (not decimal) per ticket 03; decide required-vs-optional per column; declare Glue column
  types explicitly (no crawler inference).

### 9.2 CDK stack changes

**The entire existing CDK (`KiroInfraStack.cs`, `Program.cs`, the two sample buckets) is
sample code and may be replaced wholesale** — the implementer is free to restructure the
stack rather than extend it. The current `CreateKiroBucket` helper (SSE-S3/CMK toggle,
BlockPublicAccess, EnforceSSL, RETAIN, `KiroWrite` resource policy) is worth keeping only as a
**reference** for the Kiro-writable bucket shape; reuse or rewrite it as convenient. Whatever
the structure, the POC stack must provide:

- **Raw bucket** via `CreateKiroBucket` (re-point Kiro's User Activity Report S3 location at it).
- **Analytics bucket** via the same pattern + a lifecycle rule expiring `athena-results/*`
  after ~7–14 days; no expiry on the curated table prefixes.
- **Ingest Lambda** (C#, .NET 10, zip) with the §7.3 role, reserved concurrency 1 on the
  backfill path, and the §7.4 S3 event notification (prefix + `.csv`).
- **SSM `StringList` parameter** for the Target List (`targetListParam.grantRead(fn)`).
- **Glue database `kiro_usage`** + two **static Glue tables** (`usage_daily`, `model_messages`)
  with the §5 columns, partition keys, and partition-projection table properties.
- **Athena workgroup `kiro-usage`** (§7.5).
- **Managed Grafana workspace** + data-source role (§7.6). *(Standing up the workspace/panels
  is the first post-spec step; the CDK may provision the workspace + role and leave dashboards
  as JSON to import.)*
- Program.cs already pins `Env` to `CDK_DEFAULT_ACCOUNT`/`CDK_DEFAULT_REGION` — deploy with
  the profile/region set to `369434902231` / `us-east-1`.

---

## 10. Open items / risks (carried from the fog)

- **Pipeline observability** — not yet designed: a DLQ on the Ingest Lambda, CloudWatch alarms
  on failures/parse errors, and structured logging. Revisit once the transform shape is fixed.
- **Analytics-bucket lifecycle / retention** — `athena-results/` expiry decided (~7–14 days);
  retention/storage-class for the curated fact prefixes and whether raw filtered copies are
  kept is otherwise left as-is (curated facts are the product, no expiry).
- **Multi-user behavior** — part splits, `0`-valued model cells, and multiple tiers can't be
  exercised from the single-user data; the transform must handle them defensively (tolerate
  `0` cells and drop them during Unpivot; handle `part_N` files as independent objects).
- **Cap semantics** — confirm `overage_cap` vs the true `PRO_MAX` included allowance (§8).
- **CMK path** — SSE-S3 for the POC; the additive KMS grants are documented (§7.2) if a CMK is
  required later.

---

## Source tickets

- 01 — data inspection · `issues/01-inspect-real-activity-report-data.md` (+ `assets/01-...md`)
- 02 — Parquet.Net spike · `issues/02-parquet-net-lambda-spike.md` (+ `assets/02-...md`)
- 03 — fact schemas · `issues/03-finalize-fact-schemas.md`
- 04 — backfill · `issues/04-backfill-mechanism.md`
- 05 — IAM & permissions · `issues/05-iam-and-permissions-design.md`
- 06 — Grafana design · `issues/06-grafana-workspace-design.md` (+ `assets/06-...html`)
