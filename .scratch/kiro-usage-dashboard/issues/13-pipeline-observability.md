# 13 — Pipeline observability

**What to build:** Make ingest failures visible and recoverable instead of silent. After this
ticket, a report that fails to parse or transform is captured rather than lost, and an operator
is alerted when the pipeline starts erroring.

**Blocked by:** 10 (observability wraps the ingest Lambda).

**Status:** ready-for-agent

- [ ] A dead-letter queue (SQS) on the ingest Lambda capturing events that fail after retries.
- [ ] CloudWatch alarms on Lambda errors and on transform/parse failures (metric or filtered
      log-based), so a sustained failure trips an alarm.
- [ ] Structured (JSON) logging in the transform capturing at least: source object key, rows
      read, rows kept after the Target List filter, and `usage_daily`/`model_messages` rows
      written.
- [ ] Verifiable: a deliberately malformed report lands on the DLQ and trips the error alarm;
      the structured log line for a good report shows the expected in/out counts.
