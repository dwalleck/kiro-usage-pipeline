# 10 — Ingest Lambda: live path

**What to build:** The heart of the pipeline — a C#/.NET 10 Lambda that, whenever Kiro writes a
new User Activity Report CSV, filters it to the Target List, Unpivots the dynamic model
columns, and lands query-ready Snappy Parquet for both facts. After this ticket, dropping a
real report into the raw bucket automatically produces two Parquet objects under the correct
`date=…/client_type=…` partitions (end-to-end queryable via Athena once 09 lands).

**Blocked by:** 08 (needs the raw + analytics buckets and the Target List parameter).

**Status:** ready-for-agent

- [ ] C# .NET 10 **zip** Lambda (managed .NET 10 runtime) using **Parquet.Net 6.0.3**,
      packaged/deployed by the CDK.
- [ ] Triggered by the raw bucket's `s3:ObjectCreated:*` notification, filtered to the
      `user_report/` prefix + `.csv` suffix (skips the stray UUID marker objects).
- [ ] `ProcessCsv(bucket, key)` core: parse the CSV (handling double-quoted fields incl. the
      ProfileId ARN's commas), map source columns → lowercase `snake_case` fact columns.
- [ ] **Fail-closed Target List filter**: read the SSM `StringList`, keep only rows whose
      `user_email` is on the list; a user absent from the list is never emitted.
- [ ] **Unpivot**: each `<model>_messages` column → a `model_messages` row where `model` = the
      column name minus `_messages`, verbatim lowercase (dots preserved); `auto_messages` →
      `model = "auto"` (ordinary row); **drop rows where `messages = 0`**.
- [ ] Write `usage_daily` + `model_messages` as Snappy Parquet with `date`/`client_type` as
      **path partitions only** (not in the body); credits/overage as `double`, signed
      ints/longs, date not written as INT96, one row group per file.
- [ ] **Deterministic output key** = source CSV basename with `.parquet` extension, so a
      re-fire overwrites the same object (idempotent).
- [ ] Lambda execution role scoped per the spec: `GetObject` + prefix-scoped `ListBucket` on
      the raw bucket; `PutObject` limited to `usage_daily/*` + `model_messages/*` on the
      analytics bucket; `ssm:GetParameter` on the Target List param; scoped Logs. No KMS, no
      Glue/Athena.
- [ ] Verifiable: dropping a real KIRO_CLI and a real KIRO_IDE report lands the expected
      Parquet objects under the right partitions with the correct schema.
