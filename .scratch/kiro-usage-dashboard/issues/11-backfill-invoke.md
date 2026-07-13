# 11 — Backfill invoke

**What to build:** A one-time way to process the historical reports already sitting in the raw
bucket, reusing the exact live transform. After this ticket, a single manual invoke walks every
existing `user_report/**/*.csv` object through `ProcessCsv`, so the dashboard shows full
history (`2026-06-20 → 2026-07-10`) rather than only reports that arrive after deployment.

**Blocked by:** 10 (backfill is the live per-object path in a loop — it reuses `ProcessCsv`).

**Status:** done ✅

- [x] The Lambda handler is **polymorphic**: it dispatches on event shape — an S3
      `ObjectCreated` notification → process that one key; a `{"mode":"backfill", "from":?,
      "to":?}` payload → list-and-process. Both branches converge on the same `ProcessCsv`.
- [x] Backfill lists objects under the `user_report/` prefix, filtered to the `.csv` suffix.
      Each asynchronous invocation handles one S3 page and self-invokes the next continuation;
      object failures are isolated and aggregated after later objects are attempted.
- [x] `from`/`to` bounds are optional and default to unbounded, allowing a single day to be
      reprocessed after a transform fix without a code change.
- [x] Reserved concurrency of 1 on the backfill path (or reliance on it being a single manual
      invoke) so it cannot stampede.
- [x] A small documented wrapper invokes the backfill asynchronously (`--invocation-type
      Event`) so Lambda retries and the DLQ cover backfill failures.
- [x] Verifiable: one invoke processes all 28 historical objects; re-running produces
      byte-identical overwrites (no duplicates / no double-count when queried in Athena).

## Comments

Implemented 2026-07-12:

- `BackfillRequest.cs` — DTO for backfill payload (`mode`, optional `from`/`to`).
- `Function.cs` — polymorphic handler accepting `Stream`, dispatching on `"mode": "backfill"` vs `"Records"` (S3 event).
- `Program.cs` — updated to Stream-based handler signature.
- `IIngestService.cs` — added `ProcessBackfillAsync(from?, to?, context?)`.
- `IngestService.cs` — added `rawBucket`/`rawPrefix` ctor params; implemented `ProcessBackfillAsync` with `ListObjectsV2` pagination, `.csv` suffix filter, optional `from`/`to` date bounds via `ExtractDateFromKey` (path segment parsing), and sequential `ProcessCsv` loop with progress logging.
- `IngestServiceFactory.cs` — added `RAW_BUCKET`/`RAW_PREFIX` env vars.
- `IngestPipeline.cs` — exposed `Function` property; added `RAW_BUCKET`/`RAW_PREFIX` env vars.
- `KiroInfraStack.cs` — added `IngestLambdaName` stack output for backfill script.
- `scripts/backfill.sh` — CLI wrapper: resolves Lambda name from stack output, builds payload with optional `--from`/`--to`, invokes backfill.
- Hardened in the observability follow-up: one page per async invocation, self-scheduled
  continuations, object-failure isolation, strict range/key-date validation, and 78 passing tests.
