# 02 — Spike: Parquet.Net in the .NET Lambda runtime

Type: research
Status: resolved
Blocked by: (none)

## Question

Confirm the C# Parquet path is viable before the spec commits to it. Does **Parquet.Net**
(pure-managed, no native deps) run cleanly in the .NET managed Lambda runtime and produce
**Athena-readable** Parquet?

Resolve:

- Current Parquet.Net version, target framework, and that it has no native/OS dependencies
  that break in the Lambda sandbox.
- Type mapping: how to write the fact columns (strings, ints/longs, booleans, decimals for
  credits) so Athena/Glue read them correctly; recommended logical types.
- Compression (Snappy vs gzip) support and Athena compatibility.
- Whether to package the Lambda as zip or container image given the dependency.
- A minimal proof (sample rows → Parquet → readable by Athena/`parquet-tools`), linked as an
  asset.

Deliverable: a short research note (linked) confirming feasibility and the recommended
usage pattern, or flagging a blocker with an alternative (e.g. write CSV, or use a container
image).


## Answer

**Viable — proceed with Parquet.Net.** Full research note (with citations):
[`../assets/02-parquet-net-findings.md`](../assets/02-parquet-net-findings.md).

Summary:

- **Package/runtime:** `Parquet.Net` v6.0.3, targets **.NET 8 and .NET 10**, **zero
  dependencies / pure-managed — no native binaries**, so nothing can fail to load in the
  Lambda sandbox. Package as a plain **zip** Lambda; a container image is not needed.
  **Runtime target for the build = managed .NET 10 runtime** (GA Jan 2026; .NET 8 nears EOL);
  because Parquet.Net lists .NET 10 as first-class and ships no native deps, this spike's
  conclusions carry over unchanged.
- **Type mapping:** `string`→`string`, `int`/`long`→`int`/`bigint`, `bool`→`boolean`,
  `decimal`→`decimal(p,s)`. Use signed ints only (Athena rejects unsigned `UINT_64`).
  Money: annotate `[ParquetDecimal(18,6)]` (default is 38,18); keep precision ≤ 38 and make
  the **Glue DDL column type match** the written precision/scale.
- **Compression:** default **Snappy** (Athena's Parquet default); Gzip also works. Keep Snappy.
- **Gotchas:** `DateTime` defaults to legacy **INT96** — prefer `[ParquetTimestamp]` /
  `DateTimeDataField`; watch required-vs-optional nullability; batch one row group per file
  (no tiny row groups); do Hive-style partition prefixes in the pipeline.
- **Recommended pattern:** high-level `ParquetSerializer` with fixed DTO classes for
  `usage_daily` / `model_messages` → `MemoryStream` → S3 under a partitioned prefix.

Fallback (unused): CSV for the POC if any decimal/Athena issue appears — but no blocker was found.
