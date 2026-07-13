# 09 — Glue catalog + Athena workgroup

**What to build:** The query layer over the analytics bucket — a Glue database with the two
frozen fact tables (using partition projection, no crawler) and a dedicated Athena workgroup.
After this ticket, an analyst (or Grafana) can run SQL against `usage_daily` and
`model_messages` in the `kiro-usage` workgroup; the tables read whatever Parquet the ingest
Lambda lands, with partitions resolved by projection rather than registration.

**Blocked by:** 08 (tables and workgroup results point at the analytics bucket). Parallel-able
with 10.

**Status:** resolved (deployed to `369434902231` / `us-east-1`)

- [x] Glue database `kiro_usage`.
- [x] `usage_daily` table with the 11 body columns and types per the spec, partition keys
      `date` (date) + `client_type` (string), storage location at the analytics bucket's
      `usage_daily/` prefix.
- [x] `model_messages` table with its 4 body columns, the same partition keys, storage
      location at `model_messages/`.
- [x] **Partition projection** enabled identically on both tables: `date` type=date,
      `format=yyyy-MM-dd`, `range=2026-06-01,NOW`, `interval=1 DAYS`; `client_type` type=enum,
      `values=KIRO_CLI,KIRO_IDE,PLUGIN`; `storage.location.template` with
      `date=${date}/client_type=${client_type}`.
- [x] Athena workgroup `kiro-usage`: results at `s3://<analytics-bucket>/athena-results/`,
      `EnforceWorkGroupConfiguration=true`, a `BytesScannedCutoffPerQuery` cap,
      `PublishCloudWatchMetricsEnabled=true`.
- [x] Verifiable: an Athena query against each (empty) table in the `kiro-usage` workgroup
      returns 0 rows with no schema/projection error.

## Implementation notes

Implemented as a dedicated `QueryLayer` construct (`src/KiroInfra/QueryLayer.cs`), instantiated
by `KiroInfraStack` with the analytics bucket; two `CfnOutput`s expose `GlueDatabaseName` and
`AthenaWorkGroupName`.

- **Tables:** both are `EXTERNAL_TABLE` Parquet (ParquetHiveSerDe, Mapred Parquet in/out
  formats, `classification=parquet`). `date`/`client_type` live only in `PartitionKeys`, never
  in the body (per spec §5). A shared `CreateFactTable` + `ProjectionParameters` + `Column`
  helper keeps the two tables DRY; the projection config is byte-identical between them, only
  the storage prefix differs.
- **Workgroup:** `kiro-usage`, `BytesScannedCutoffPerQuery` = 1 GiB (the §7.5 "modest cap"),
  `RecursiveDeleteOption=true` for clean POC teardown.
- The C# `${{date}}`/`${{client_type}}` doubled braces emit the literal Athena projection
  placeholders `${date}`/`${client_type}`; the prefix ends in `/` so there's no double slash.

**Deployed** to `369434902231` / `us-east-1` (stack `KiroInfraStack`, UPDATE_COMPLETE).
Live-verified in the `kiro-usage` workgroup: `SELECT COUNT(*)` = 0 and `SELECT * LIMIT 10` = 0
rows on both `usage_daily` and `model_messages`, all `SUCCEEDED`, correct headers
(`usage_daily` 13 cols incl. `date`+`client_type`; `model_messages` 6 cols), no
schema/projection error.
