# 09 — Glue catalog + Athena workgroup

**What to build:** The query layer over the analytics bucket — a Glue database with the two
frozen fact tables (using partition projection, no crawler) and a dedicated Athena workgroup.
After this ticket, an analyst (or Grafana) can run SQL against `usage_daily` and
`model_messages` in the `kiro-usage` workgroup; the tables read whatever Parquet the ingest
Lambda lands, with partitions resolved by projection rather than registration.

**Blocked by:** 08 (tables and workgroup results point at the analytics bucket). Parallel-able
with 10.

**Status:** ready-for-agent

- [ ] Glue database `kiro_usage`.
- [ ] `usage_daily` table with the 11 body columns and types per the spec, partition keys
      `date` (date) + `client_type` (string), storage location at the analytics bucket's
      `usage_daily/` prefix.
- [ ] `model_messages` table with its 4 body columns, the same partition keys, storage
      location at `model_messages/`.
- [ ] **Partition projection** enabled identically on both tables: `date` type=date,
      `format=yyyy-MM-dd`, `range=2026-06-01,NOW`, `interval=1 DAYS`; `client_type` type=enum,
      `values=KIRO_CLI,KIRO_IDE,PLUGIN`; `storage.location.template` with
      `date=${date}/client_type=${client_type}`.
- [ ] Athena workgroup `kiro-usage`: results at `s3://<analytics-bucket>/athena-results/`,
      `EnforceWorkGroupConfiguration=true`, a `BytesScannedCutoffPerQuery` cap,
      `PublishCloudWatchMetricsEnabled=true`.
- [ ] Verifiable: an Athena query against each (empty) table in the `kiro-usage` workgroup
      returns 0 rows with no schema/projection error.
