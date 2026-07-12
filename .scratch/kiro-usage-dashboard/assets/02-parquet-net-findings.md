# Parquet.Net in a .NET 8 Lambda — Feasibility Findings

Research note for ticket `02-parquet-net-lambda-spike`. Question: can **Parquet.Net**
run in the managed .NET Lambda runtime and produce **Athena-readable** Parquet for the
Daily Usage Fact / Model Message Fact tables?

**Verdict: Viable.** Parquet.Net is pure-managed with zero dependencies, targets .NET 8,
and produces standard Parquet that Athena/Glue read natively. Recommended for the POC.

> **Runtime update (post-spike):** the POC targets the **managed .NET 10 Lambda runtime**
> (GA Jan 2026; .NET 8 nears end of maintenance). This spike was written against .NET 8, but
> Parquet.Net explicitly lists **.NET 10 as a first-class target** (§1) and has **zero native
> deps**, so every conclusion below — viability, type mapping, Snappy, zip packaging, gotchas
> — transfers to .NET 10 unchanged. Read ".NET 8" below as ".NET 10" for the implementation.

## 1. Version, target framework, dependencies

- **NuGet package id:** `Parquet.Net` — current version **6.0.3**.
  Source: [nuget.org/packages/Parquet.Net](https://www.nuget.org/packages/Parquet.Net).
- **Target frameworks:** "Targets only modern .NET runtimes such as .NET 10 and .NET 8."
  So **.NET 8 is a first-class target** — matches the CDK/Lambda runtime in this repo.
  Source: [github.com/aloneguid/parquet-dotnet](https://github.com/aloneguid/parquet-dotnet).
- **Dependencies:** "Has zero dependencies - pure library that just works anywhere .NET
  works." It is explicitly *not* a wrapper over a native (C++) library.
  Source: same README as above.
- **Lambda implication:** because it is 100% managed IL with **no native/OS `.so`
  binaries**, there is nothing that can fail to load in the Lambda sandbox regardless of
  packaging choice. This is the key property that makes it safe here (contrast with
  ParquetSharp/pyarrow which ship native binaries).
  _Content was rephrased for compliance with licensing restrictions._

## 2. Type mapping for the fact columns

Parquet.Net maps CLR types to Parquet primitive + logical types automatically. Recommended
schema types for our columns:

| Fact column (example)             | CLR type    | Parquet result                          | Athena/Glue type |
|-----------------------------------|-------------|-----------------------------------------|------------------|
| User Email, Client Type, Model    | `string`    | `BYTE_ARRAY` + `STRING`/UTF8 annotation | `string`         |
| Date                              | `DateTime`  | see date gotcha below                   | `timestamp`/`date` |
| conversations, total messages     | `int`/`long`| `INT32` / `INT64`                       | `int` / `bigint` |
| overage flag                      | `bool`      | `BOOLEAN`                               | `boolean`        |
| credits / overage (money)         | `decimal`   | `DECIMAL` logical type                  | `decimal(p,s)`   |

Notes / recommended usage:

- **string** — serialized as optional (nullable) by default; annotate `[ParquetRequired]`
  if a column must be non-null. Physical type is `BYTE_ARRAY` with the UTF8/STRING
  annotation, which Athena reads as `string`.
- **int / long** — use `int` (`INT32` → Athena `int`) and `long` (`INT64` → Athena
  `bigint`). **Avoid unsigned types** (`uint`/`ulong`): Athena rejects `UINT_64` with
  "Parquet type not supported: INT64 (UINT_64)".
  Source: [stackoverflow.com/questions/63395794](https://stackoverflow.com/questions/63395794/uint-64-causing-errors-in-athena).
- **bool** — maps to Parquet `BOOLEAN` → Athena `boolean`.
- **decimal (credits / overage $)** — Parquet.Net defaults to **precision 38, scale 18**.
  Override per-column with `[ParquetDecimal(precision, scale)]` (high-level API) or
  `DecimalDataField(name, precision, scale)` (low-level API). For currency, a scale like
  `decimal(18,6)` or `decimal(38,10)` is a sensible explicit choice.
  Source: parquet-dotnet README (Customising serialization → `decimal`).
  - **DECIMAL caveats for Athena/Glue:**
    - Keep **precision ≤ 38** (Parquet default 38 is the max; Athena `decimal` also caps
      at 38). Parquet DECIMAL logical type is defined over INT32/INT64/FIXED_LEN_BYTE_ARRAY
      physical types. Source:
      [parquet.apache.org logical types](https://parquet.apache.org/docs/file-format/types/logicaltypes/).
    - The **Glue table column type must declare the same precision/scale** you wrote
      (e.g. `decimal(18,6)`). A mismatch, or letting a Glue crawler guess, is a common
      cause of wrong/blank decimals — declare the column type explicitly in the DDL rather
      than relying on crawler inference. Source:
      [repost.aws — enforce column type in Glue crawler](https://repost.aws/questions/QUKtWpy9-RR3G4W_cBTufu2Q/enforce-column-type-in-glue-crawler).
    - If Athena has any trouble with a fixed-len-byte-array decimal, the pragmatic POC
      fallback is to store money as `double` or as `decimal(18,6)`; for a usage dashboard
      `double` is usually acceptable. Source (general Athena decimal handling):
      [stackoverflow.com/questions/57629314](https://stackoverflow.com/questions/57629314/aws-glue-or-athena-or-presto-changing-decimal-format).

## 3. Compression

- Parquet.Net "Supports all parquet types, encodings and compressions." Compression is set
  on `ParquetWriter` via `CompressionMethod` / `CompressionLevel` and **defaults to Snappy**.
  Source: parquet-dotnet README (Extra options).
- **Athena compatibility:** Snappy is the default and recommended compression for Parquet
  in Athena; Gzip is also fully supported, and Athena can even read a table where different
  Parquet files use different codecs (Snappy + Gzip mixed).
  Sources:
  [Athena — use compression](https://docs.aws.amazon.com/athena/latest/ug/compression-formats.html),
  [Athena Hive compression support](https://docs.aws.amazon.com/athena/latest/ug/compression-support-hive.html).
- **Recommendation:** keep the Parquet.Net default **Snappy** — good balance of ratio vs
  CPU (matters for Lambda runtime/cost) and it is Athena's Parquet default.

## 4. Packaging: zip vs container image

- Because Parquet.Net is pure managed with **no native assets**, it adds no packaging
  constraints. A standard **managed .NET 8 zip Lambda** (`dotnet lambda package` /
  `Code.fromAsset` in CDK) is sufficient and is the simpler, faster-cold-start option.
- **Recommendation: zip.** Reserve container images for cases needing >250 MB unzipped or
  custom native binaries — neither applies here. (If the team later prefers a uniform
  container workflow that's fine too, but it is not required by this dependency.)

## 5. Known gotchas writing Parquet consumed by Athena

1. **Dates default to INT96.** Parquet.Net serializes `DateTime` as legacy `INT96` by
   default. Athena/Hive do read INT96 as timestamp, but INT96 is deprecated. Prefer an
   explicit modern type: high-level `[ParquetTimestamp]` (ms precision) or low-level
   `DateTimeDataField(name, DateTimeFormat.Date)` for a date-only column. Map to Athena
   `timestamp` (or `date`). Source: parquet-dotnet README (Customising serialization →
   Dates; Special cases).
2. **Nullability mismatch.** Strings/reference types are optional by default; if you mark a
   column required in Glue but write it optional (or vice-versa) you can get read errors.
   Decide required vs optional per column and use `[ParquetRequired]` where needed.
3. **Unsigned integers are not Athena-readable** (see §2) — stick to signed `int`/`long`.
4. **Decimal precision/scale must match the Glue DDL** (see §2) — declare table column
   types explicitly instead of trusting crawler inference.
5. **Row-group sizing.** Don't write tiny row groups (e.g. one per row); it bloats files
   and kills read performance. Batch a day's rows into a single row group. Source:
   parquet-dotnet README (Specifying row group size / Appending).
6. **Partitioning is the pipeline's job, not Parquet.Net's** — Parquet.Net writes one file;
   lay files out under Hive-style prefixes (e.g. `.../date=YYYY-MM-DD/`) for Athena
   partition pruning. (Design note, not a library limitation.)

## Recommended usage pattern (POC)

- Use the **high-level `ParquetSerializer`** with plain C# DTO classes for `usage_daily`
  and `model_messages` (fixed schemas → serialization is fast after first call).
- Annotate money columns with `[ParquetDecimal(18,6)]` (or accept default 38,18) and date
  columns with `[ParquetTimestamp]`; use signed ints/longs and `string` labels.
- Default **Snappy** compression, one row group per output file, write to a `MemoryStream`
  then `PutObject` to S3 under a partitioned prefix.
- Package as a **zip** .NET 8 Lambda via CDK.
- Declare Glue table column types explicitly (matching decimal precision/scale) rather than
  relying on a crawler.

No blocker found; CSV fallback is unnecessary for the POC but remains a trivial escape hatch
if any decimal/Athena issue surfaces during implementation.

## Sources

- Parquet.Net GitHub README — https://github.com/aloneguid/parquet-dotnet
- Parquet.Net on NuGet (v6.0.3, TFMs) — https://www.nuget.org/packages/Parquet.Net
- Apache Parquet logical types (DECIMAL) — https://parquet.apache.org/docs/file-format/types/logicaltypes/
- Athena — use compression — https://docs.aws.amazon.com/athena/latest/ug/compression-formats.html
- Athena — Hive compression support — https://docs.aws.amazon.com/athena/latest/ug/compression-support-hive.html
- Athena UINT_64 not supported — https://stackoverflow.com/questions/63395794/uint-64-causing-errors-in-athena
- Glue crawler enforce column type — https://repost.aws/questions/QUKtWpy9-RR3G4W_cBTufu2Q/enforce-column-type-in-glue-crawler
- Athena/Glue/Presto decimal handling — https://stackoverflow.com/questions/57629314/aws-glue-or-athena-or-presto-changing-decimal-format
