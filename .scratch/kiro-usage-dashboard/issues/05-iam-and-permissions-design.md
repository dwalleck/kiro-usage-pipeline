# 05 — Design IAM & permissions for the pipeline

Type: grilling
Status: resolved
Blocked by: 01

## Question

Define the least-privilege permissions for every hop, all in-account (`369434902231`).

Resolve:

- **Lambda execution role**: read the source bucket(s)/prefix (confirm whether an existing
  source-bucket *policy* change is needed, or the role suffices since it's same-account),
  write the target bucket, `ssm:GetParameter` on the Target List parameter, KMS if the
  target bucket uses a CMK, and CloudWatch Logs.
- **Target bucket** protections: SSE-S3 default with KMS toggle (mirroring
  `KiroInfraStack.cs`), Block Public Access, `EnforceSSL`.
- **Athena/Glue access**: who can query; workgroup + query-results location and its
  permissions (may pull in the "Athena workgroup" fog item).
- **Managed Grafana → Athena**: the Grafana workspace's IAM role for the Athena data source,
  and how humans authenticate to Grafana (IAM Identity Center vs SAML) — coordinate with
  ticket 06.

Deliverable: the role/policy design (as CDK-ready statements) for the pipeline and the
query/dashboard layer.


## Answer

Resolved by grilling. All in-account (`369434902231`), **single region `us-east-1`** — the
whole POC collapses onto the source region (see decision 1), so every statement below is
single-region/single-account. Buckets are **new, CDK-managed** (the data migrates off the
pre-existing `kiro-monitoring-*` buckets and Kiro is re-pointed at the CDK bucket).

### 0. Decisions (branch tree)

1. **Region** — collapse the entire POC (Lambda, buckets, Glue/Athena, Grafana) into
   **`us-east-1`**, co-located with the source. Reverses the map's original `us-east-2` note
   (which predated ticket 01 finding the source in us-east-1). Kills the cross-region read and
   cross-region event wiring; every IAM statement is single-region/single-account.
2. **Trigger** — **event-driven** S3 `s3:ObjectCreated:*` notification → Lambda, filtered to
   the `user_report/` prefix and `.csv` suffix (skips the stray marker objects from ticket 01).
   Clean because the source bucket is now CDK-managed (no import hacks).
3. **Bucket topology** — **two buckets**: (1) **raw** source (existing `CreateKiroBucket`
   pattern, Kiro writes via the `q.amazonaws.com` policy); (2) **analytics** bucket holding
   curated Parquet under `usage_daily/` + `model_messages/` and Athena results under
   `athena-results/` (short-expiry lifecycle on that prefix).
4. **Target List** — **SSM Parameter Store `StringList`, Standard tier, plain (not
   SecureString)**. Emails aren't secret → no KMS on the parameter; editable without a
   redeploy. Lambda gets `ssm:GetParameter` on the single parameter ARN. Fail-closed is a
   Lambda concern, not IAM.
5. **Encryption** — **SSE-S3 default** (`UseCustomKey=false`) on both buckets → zero KMS
   statements anywhere. Design stays **CMK-ready**: the analytics bucket honors the same
   `UseCustomKey` toggle as the raw bucket, and the KMS grants each principal would need are
   documented in §5 below so flipping it on is additive.
6. **Source read** — **identity policy on the Lambda role only**; the raw bucket's resource
   policy is left untouched (same-account: identity grant suffices, no Deny blocks it,
   `EnforceSSL` satisfied by the SDK's TLS). `GetObject` + prefix-scoped `ListBucket` (one role
   covers both steady-state and backfill).
7. **Athena** — **dedicated workgroup `kiro-usage`**: results at
   `s3://<analytics>/athena-results/`, `EnforceWorkGroupConfiguration=true`, a modest
   `BytesScannedCutoffPerQuery` guardrail, `PublishCloudWatchMetricsEnabled=true`. Catalog
   `AwsDataCatalog` (Glue); database = the CDK-created Glue DB holding the two tables. Human
   querying already works via the `AdministratorAccess-369434902231` permission set.
8. **Grafana** — data-source role = **`AmazonGrafanaAthenaAccess`** managed policy **+** a
   tight inline policy scoping S3 to this analytics bucket's curated + results prefixes and
   pinning the `kiro-usage` workgroup. Human auth = **IAM Identity Center** (already in use;
   you assigned as workspace Admin). Okta later = the **SAML** path — either Grafana→Okta
   directly, or Okta→Identity Center as upstream IdP (leaves Grafana auth untouched); choosing
   Identity Center now keeps that door open. Coordinates with ticket 06.

### 1. Lambda execution role (CDK-ready statements)

Trust: `lambda.amazonaws.com`. Region `us-east-1`, account `369434902231`.

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

# Logs (scoped to the function log group)
Allow logs:CreateLogGroup, logs:CreateLogStream, logs:PutLogEvents
  on arn:aws:logs:us-east-1:369434902231:log-group:/aws/lambda/<fn-name>:*
```

- **No KMS** (SSE-S3). **No Glue/Athena** — partition projection (ticket 03) means the Lambda
  never registers partitions, so it needs zero catalog access; it just writes to the projected
  `date=…/client_type=…` paths.
- CDK sugar: `rawBucket.grantRead(fn, ".../user_report/*")`,
  `analyticsBucket.grantWrite(fn, "usage_daily/*")` + `"model_messages/*"`,
  `targetListParam.grantRead(fn)` — but keep the write grant scoped to the two prefixes, not
  the whole bucket.

### 2. Trigger wiring

```
rawBucket.addEventNotification(
  EventType.OBJECT_CREATED,
  new LambdaDestination(fn),
  { prefix: "<prefix>/AWSLogs/369434902231/KiroLogs/user_report/", suffix: ".csv" });
```

CDK auto-adds the `s3.amazonaws.com` `lambda:InvokeFunction` permission scoped to the raw
bucket ARN + `aws:SourceAccount`. Both bucket and function are in `us-east-1`, so the
same-region notification constraint is satisfied.

### 3. Raw (source) bucket — unchanged pattern

Reuse `CreateKiroBucket`: `BlockPublicAccess.BLOCK_ALL`, `EnforceSSL=true`,
`RemovalPolicy.RETAIN`, SSE-S3 (or CMK via toggle), `KiroWrite` resource policy allowing
`q.amazonaws.com` `s3:PutObject` scoped to `aws:SourceAccount` + the `codewhisperer` service
ARN. No new statement for the Lambda (identity policy handles read, §1).

### 4. Analytics (target) bucket — new

Same protections as the raw bucket: `BlockPublicAccess.BLOCK_ALL`, `EnforceSSL=true`,
SSE-S3 default (honors the `UseCustomKey` toggle), `RemovalPolicy.RETAIN`. No public/no
cross-account grants. Lifecycle: **expire `athena-results/*` after ~7–14 days** (results are
disposable). Optional: expire nothing under the table prefixes (curated facts are the product).
Access is via identity policies on the Lambda role (write) and the Grafana role (read) — no
bucket policy statements needed (all same-account).

### 5. Athena workgroup + querying

- Workgroup `kiro-usage`: `ResultConfiguration.OutputLocation =
  s3://<analytics-bucket>/athena-results/`, `EnforceWorkGroupConfiguration=true`,
  `BytesScannedCutoffPerQuery` = small cap, `PublishCloudWatchMetricsEnabled=true`.
- Human console querying: covered by the existing `AdministratorAccess-369434902231`
  permission set — no new IAM.
- The machine querier is the Grafana role (§6).

### 6. Managed Grafana → Athena data-source role

Trust: `grafana.amazonaws.com` (workspace service role). Attach **`AmazonGrafanaAthenaAccess`**
+ this scoped inline policy:

```
# Athena — pinned to the workgroup
Allow athena:StartQueryExecution, athena:StopQueryExecution,
      athena:GetQueryExecution, athena:GetQueryResults, athena:GetWorkGroup
  on arn:aws:athena:us-east-1:369434902231:workgroup/kiro-usage

# Glue catalog reads (projection ⇒ no GetPartitions needed, but harmless)
Allow glue:GetDatabase, glue:GetTable, glue:GetTables, glue:GetPartitions
  on catalog, database/<db>, table/<db>/usage_daily, table/<db>/model_messages

# S3 — read curated
Allow s3:GetObject
  on arn:aws:s3:::<analytics-bucket>/usage_daily/*
  on arn:aws:s3:::<analytics-bucket>/model_messages/*
Allow s3:ListBucket
  on arn:aws:s3:::<analytics-bucket>
  Condition StringLike { s3:prefix: ["usage_daily/*","model_messages/*","athena-results/*"] }

# S3 — read/write Athena results
Allow s3:GetObject, s3:PutObject
  on arn:aws:s3:::<analytics-bucket>/athena-results/*
```

Human auth: **IAM Identity Center**, you assigned as workspace **Admin**. (Okta → SAML path,
see decision 8.)

### CMK-ready delta (only if `UseCustomKey=true`)

- Lambda role: `+ kms:Decrypt` (read raw) `+ kms:GenerateDataKey, kms:Decrypt` (write curated).
- Grafana role: `+ kms:Decrypt, kms:GenerateDataKey` (Athena reads curated + writes results).
- Key policy: add the Lambda role and Grafana role as principals for those actions (alongside
  the existing `q.amazonaws.com` grant). Everything else is unchanged.

### Consequences for other tickets

- **04 (backfill)** — the `s3:ListBucket` (prefix-scoped) + `s3:GetObject` on the Lambda role
  already cover backfill enumeration; one role, one code path.
- **06 (Grafana)** — auth = IAM Identity Center, data source uses the role in §6; consume the
  `kiro-usage` workgroup + `athena-results/` location.
- **Fog resolved**: cross-region trigger wiring (now moot — single region, §2) and the Athena
  workgroup + query-results location (§5).
