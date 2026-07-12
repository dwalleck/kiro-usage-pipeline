# 08 — CDK foundation: buckets + Target List

**What to build:** Replace the throwaway sample CDK with the real POC stack skeleton — the two
S3 buckets the pipeline reads from and writes to, plus the Target List parameter the transform
filters on. After this ticket, `cdk deploy` in `us-east-1` stands up an empty but correctly
protected data plane, and Kiro can be re-pointed to write User Activity Reports into the new
raw bucket.

**Blocked by:** None — can start immediately.

**Status:** resolved (deployed to `369434902231` / `us-east-1`)

- [x] Existing sample CDK is treated as replaceable: the throwaway sample buckets
      (`kiro-prompt-logs`, `kiro-activity-reports`) no longer provisioned; stack restructured
      as needed (the `CreateKiroBucket`-style Kiro-writable shape may be reused or rewritten).
- [x] **Raw bucket** (CDK-managed) with `BlockPublicAccess.BLOCK_ALL`, `EnforceSSL=true`,
      `RemovalPolicy.RETAIN`, SSE-S3 default, and the `KiroWrite` resource policy allowing
      `q.amazonaws.com` `s3:PutObject` scoped to `aws:SourceAccount` + the `codewhisperer`
      service ARN. A `CfnOutput` emits its S3 URI for re-pointing Kiro's report location.
- [x] **Analytics bucket** (CDK-managed) with the same protections, plus a lifecycle rule
      expiring `athena-results/*` after ~7–14 days and no expiry on the curated table prefixes.
- [x] **Target List** stored as an SSM Parameter Store `StringList` (Standard tier, plain —
      not SecureString), seeded with `dwalleck@proton.me`.
- [x] `UseCustomKey` toggle honored on both buckets (SSE-S3 when off; the CMK path stays
      additive per the spec's documented delta).
- [x] `cdk deploy` succeeds against account `369434902231` / `us-east-1`; both buckets and the
      parameter exist with the policies above.

## Implementation notes

Implemented in `src/KiroInfra/KiroInfraStack.cs` (full rewrite of the sample stack).

- **Buckets:** `kiro-usage-raw-<acct>-<region>` (raw) and `kiro-usage-analytics-<acct>-<region>`
  (analytics), both via a shared `CreateProtectedBucket` helper (SSE-S3 / CMK toggle,
  `BLOCK_ALL`, `EnforceSSL`, `RETAIN`). The `KiroWrite` policy is applied only to the raw
  bucket (Kiro doesn't write to analytics). Analytics carries the `ExpireAthenaResults`
  lifecycle rule (14 days, prefix `athena-results/`); curated prefixes are never expired.
- **Target List:** SSM `StringListParameter` at `/kiro-usage/target-list`, Standard tier,
  plain, seeded `["dwalleck@proton.me"]`.
- **Outputs:** `RawBucketUri` (for re-pointing Kiro's report S3 location), `AnalyticsBucketName`,
  `TargetListParameterName`.
- **CMK path:** the confused-deputy `KiroSourceConditions()` guard (aws:SourceAccount +
  codewhisperer ARN) is shared by the S3 and KMS key policies. The Lambda/Grafana KMS grants
  from spec §7.2 are deferred to tickets 09–13 (those principals don't exist yet).

**Verification:** `dotnet build` clean; synthesized via `dotnet run` (no `cdk` CLI in the env)
for both `UseCustomKey=false` (SSE-S3/AES256 on both buckets, zero KMS) and `UseCustomKey=true`
(one KMS key, both buckets `aws:kms`). Template asserts: KiroWrite policy on raw only, 14-day
`athena-results/` expiry, `StringList` param.

**Deployed** to `369434902231` / `us-east-1` (stack `KiroInfraStack`, `UseCustomKey=false`).
Live-verified: raw bucket policy carries the `KiroWrite` + Deny-non-TLS statements; analytics
bucket has the `ExpireAthenaResults` 14-day rule on `athena-results/`; SSM `/kiro-usage/target-list`
is a `StringList` = `dwalleck@proton.me`. Outputs — `RawBucketUri` =
`s3://kiro-usage-raw-369434902231-us-east-1/` (re-point Kiro's User Activity Report location here),
`AnalyticsBucketName` = `kiro-usage-analytics-369434902231-us-east-1`.
