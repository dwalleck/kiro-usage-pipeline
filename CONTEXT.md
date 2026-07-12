# Kiro Usage Analytics

This repo builds a custom pipeline and dashboard over the Kiro usage data that Kiro delivers into S3 buckets in our own AWS account. It filters that data to a chosen subset of users and exposes it for querying.

## Language

### Data sources

**User Activity Report**:
The daily per-user CSV that Kiro writes under `user_report/`, one file per client type (`KIRO_CLI`, `KIRO_IDE`, `PLUGIN`) per day. Carries usage metrics and, crucially, `User_Email`. This is the primary source for the dashboard.
_Avoid_: usage log, telemetry file, metrics dump.

**Legacy Analytic Report**:
The older CSV Kiro still writes under `by_user_analytic/` — CLI and plugin usage only, with detailed code-acceptance metrics but **no email and no client type**, and a different date format (`MM-DD-YYYY`).
_Avoid_: old report.

**Prompt Log**:
A gzipped-JSON file under `prompt-logs/`, one conversation event per file, containing the actual prompt and response **content**. Keyed by `userId` only — no email. Out of scope for v1.
_Avoid_: chat log, transcript.

### Identity

**User Id**:
The bare IAM Identity Center user identifier as it appears in the activity reports, e.g. `215bb5b0-00a1-70cd-1caf-57794fdc8915`.
_Avoid_: user sub, principal id.

**Prefixed User Id**:
The identity form used inside Prompt Logs, `d-<directoryId>.<UserId>`, e.g. `d-9a673a2cf5.215bb5b0-...`. The segment after the dot is exactly the **User Id**. This is the sole bridge between Prompt Logs and email.

**User Email**:
The user's email address (`User_Email` column). Present only in the User Activity Report; it is the field we filter the pipeline on.

### Pipeline

**Target User**:
A user whose usage we are authorized to extract — a member of the Target List, identified by User Email. Anyone not on the list is dropped by the pipeline.
_Avoid_: subject user, allowed user.

**Target List**:
The explicit allowlist of User Emails the pipeline keeps. The pipeline fails closed: a user absent from the list is never included, even if new.
_Avoid_: allow list (as two words), filter list.

### Analytics tables

**Daily Usage Fact** (`usage_daily`):
One row per (Date, User Id, Client Type) holding the user/day/client-grain metrics — credits, conversations, total messages, subscription tier, overage. Also carries **User Email** as a human-readable label so dashboards are legible without a separate lookup (every row is already a Target User). Its schema is fixed and never changes when new models appear.
_Avoid_: usage table, summary.

**Model Message Fact** (`model_messages`):
The dynamic `<model>_messages` columns of the User Activity Report unpivoted to long form — one row per (Date, User Id, Client Type, Model), also carrying **User Email** as a label. A newly-used model becomes a new *row*, never a new column.
_Avoid_: model table, pivot table.

**Unpivot**:
The pipeline step that turns the variable set of `<model>_messages` columns in a report into Model Message Fact rows, keeping the schema stable across time.
_Avoid_: melt, explode.
