# 10 — Ingest Lambda: live path

**What to build:** The heart of the pipeline — a C#/.NET 10 Lambda that, whenever Kiro writes a
new User Activity Report CSV, filters it to the Target List, Unpivots the dynamic model
columns, and lands query-ready Snappy Parquet for both facts. After this ticket, dropping a
real report into the raw bucket automatically produces two Parquet objects under the correct
`date=…/client_type=…` partitions (end-to-end queryable via Athena once 09 lands).

**Blocked by:** 08 (needs the raw + analytics buckets and the Target List parameter).

**Status:** done ✅

- [x] C# .NET 10 **zip** Lambda (managed .NET 10 runtime) using **Parquet.Net 6.0.3**,
      packaged/deployed by the CDK. (Deployed 2026-07-12; Docker bundling via `mcr.microsoft.com/dotnet/sdk:10.0`.)
- [x] Triggered by the raw bucket's `s3:ObjectCreated:*` notification, filtered to the
      `user_report/` prefix + `.csv` suffix (skips the stray UUID marker objects).
- [x] `ProcessCsv(bucket, key)` core: parse the CSV (handling double-quoted fields incl. the
      ProfileId ARN's commas), map source columns → lowercase `snake_case` fact columns.
- [x] **Fail-closed Target List filter**: read the SSM `StringList`, keep only rows whose
      `user_email` is on the list; a user absent from the list is never emitted.
- [x] **Unpivot**: each `<model>_messages` column → a `model_messages` row where `model` = the
      column name minus `_messages`, verbatim lowercase (dots preserved); `auto_messages` →
      `model = "auto"` (ordinary row); **drop rows where `messages = 0`**.
- [x] Write `usage_daily` + `model_messages` as Snappy Parquet with `date`/`client_type` as
      **path partitions only** (not in the body); credits/overage as `double`, signed
      ints/longs, date not written as INT96, one row group per file.
- [x] **Deterministic output key** = source CSV basename plus a short SHA-256 suffix of the
      full source bucket/key. Re-fires overwrite the same object, while distinct source paths
      cannot collide; reprocessing deletes obsolete outputs.
- [x] Lambda execution role scoped per the spec: `GetObject` + prefix-scoped `ListBucket` on
      the raw bucket; `PutObject` limited to `usage_daily/*` + `model_messages/*` on the
      analytics bucket; `ssm:GetParameter` on the Target List param; scoped Logs. No KMS, no
      Glue/Athena.
- [x] Verifiable: dropping a real KIRO_CLI and a real KIRO_IDE report lands the expected
      Parquet objects under the right partitions with the correct schema.
      > Verified 2026-07-12: copied two real reports (2026-06-22 KIRO_CLI + KIRO_IDE) to raw bucket.
      > Both triggered the Lambda (cold: 2531ms, warm: 224ms), landed 4 Parquet objects under
      > `usage_daily/date=2026-06-22/client_type=KIRO_{CLI,IDE}/` and
      > `model_messages/date=2026-06-22/client_type=KIRO_{CLI,IDE}/`.
      > Athena `SELECT * FROM usage_daily` returns 2 rows; `SELECT * FROM model_messages` returns 3 rows
      > (CLI: claude_opus_4.6=49 + claude_opus_4.8=251; IDE: auto=9). Partition projection resolves correctly.
