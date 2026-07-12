# 04 — Decide the one-time backfill mechanism

Type: grilling
Status: resolved
Blocked by: 01

## Question

The pipeline is event-driven (S3 `ObjectCreated` on the `user_report/` prefix), so it only
sees *new* CSVs. How do we process the historical reports already sitting in the source
bucket, once?

Weigh the options against what ticket 01 finds (how many objects, how far back):

- **On-demand backfill invoke**: a Lambda entry point that lists existing `user_report/`
  objects and processes them through the same transform code (reused, not duplicated).
- **Re-emit S3 events** / S3 Batch Operations to replay `ObjectCreated`.
- **Manual one-off script** run locally against the same code path.

Also decide: does backfill share the exact transform + deterministic-key logic as the live
path (it should, for idempotency)? Any throttling/concurrency concerns at the observed
object count?

Deliverable: the chosen backfill approach and how it reuses the live transform.


## Answer

**Chosen approach: an on-demand invoke of the *live* pipeline Lambda that lists the
historical objects and runs each through the identical transform + deterministic-key
writer.** Backfill is not a separate program — it is "the live per-object path, in a loop."

Grounding (ticket 01): the backfill target is tiny — **28 CSV objects**, `2026-06-20 →
2026-07-10`, ~400 bytes / 1 row each, single user (`dwalleck@proton.me`), plus three stray
103-byte UUID marker objects under `KiroLogs/` to ignore.

### Decisions (grilled, one branch at a time)

1. **Mechanism — on-demand Lambda invoke** (not S3 Batch Ops / event re-emit, not a local
   script). Rationale: at 28 tiny objects this is about *code + environment fidelity*, not
   throughput. Running the same function means zero drift from the production runtime, IAM
   role, and .NET host, and nothing extra to package. Event re-emit / Batch Operations add
   moving parts for no benefit; a local script reuses the code but not the environment.

2. **Structure — one polymorphic Lambda function.** The handler inspects the incoming event:
   an S3 `ObjectCreated` notification → process that one key; a custom backfill payload →
   list-and-process. Both branches converge on a single private `ProcessCsv(bucket, key)`
   core. One deployable, one role, one log group, one place bugs get fixed. Cost: the
   handler input is a union (S3 event *or* custom JSON), so it deserializes as a
   stream/`JObject` and branches — minor plumbing.

3. **Trigger — manual, on-demand CLI invoke after `cdk deploy`** (wrapped in a small
   script), e.g. `aws lambda invoke --function-name … --payload '{"mode":"backfill"}'`.
   Backfill is a genuinely one-time data operation and is deliberately **not** entangled
   with the infra lifecycle: no CDK custom resource auto-run on deploy (would make every
   deploy a potential re-trigger and complicate rollback), no lingering one-shot EventBridge
   rule. Manual = auditable and fired precisely when chosen; deterministic keys make it
   safely repeatable.

4. **Idempotency — identical deterministic-key overwrite path** (shared with live, per
   ticket 03). Output key is a pure function of the **source object's identity** (date +
   client_type + user), *not* wall-clock or a run id. Therefore: a failed partial backfill
   is fixed by simply re-running; an object that gets both backfilled and later
   live-processed lands on the same key with identical content (S3 last-writer-wins). No
   duplicates, no dedup step, no Athena double-counting.

5. **Concurrency — single invoke, sequential `foreach` loop.** 28 sub-KB objects is
   milliseconds of work, well inside the timeout, with negligible S3 request rates — nothing
   to throttle and no contention with the live path. Guardrails named for safety, not need:
   **reserved concurrency 1** on the backfill path (or simply that it is a single manual
   invoke); paging the S3 listing + continuation-token re-invoke kept only as a
   never-triggered fallback should the object count ever approach the timeout.

6. **Selection scope — full-prefix scan under `user_report/`, filtered to the `.csv`
   suffix, no date bound by default.** The `user_report/**` + `.csv` filter naturally
   excludes the stray marker objects (which sit above `user_report/` and lack `.csv`) with
   no special-casing. The **Target-List email filter stays *inside* `ProcessCsv`**, so
   backfill and live apply identical filtering. Payload shaped as
   `{"mode":"backfill", "from":?, "to":?}` with bounds **optional / default unbounded**, so a
   single day can be reprocessed after a transform fix without a code change.

### How it reuses the live transform (the deliverable)

Backfill and live are the *same code path*: the polymorphic handler dispatches on event
shape and both call `ProcessCsv(bucket, key)`, which performs the CSV parse, Target-List
email filter, Unpivot, and the deterministic-key Parquet writes for `usage_daily` and
`model_messages`. Backfill adds only a thin outer loop (S3 list under `user_report/`, filter
to `*.csv`, optional date-range trim) around that shared core. Because the write keys are
deterministic functions of source identity, the loop is idempotent and freely re-runnable.
