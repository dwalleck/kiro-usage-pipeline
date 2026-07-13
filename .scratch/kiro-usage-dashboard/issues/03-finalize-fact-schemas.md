# 03 — Finalize the fixed schemas for the two facts

Type: grilling
Status: resolved
Blocked by: 01, 02

## Question

Pin down the exact, fixed column list and types for **Daily Usage Fact** (`usage_daily`) and
**Model Message Fact** (`model_messages`), derived from the real data (ticket 01) and the
Parquet type mapping (ticket 02).

Resolve:

- `usage_daily` columns: which static User Activity Report fields map through, their Parquet
  types, and the grain (Date, User Id, Client Type) plus the carried `User Email` label.
- `model_messages` columns after the Unpivot: (Date, User Id, Client Type, Model, message
  count) + `User Email`, and how `Auto` and zero-value models are handled (drop zero rows or
  keep them?).
- Types for tricky fields: credits/overage (decimal vs int), booleans, subscription tier
  (string enum).
- The partition columns as physically laid out on S3 (date + client_type) vs columns stored
  in the Parquet body.
- Confirm the schemas are stable across new models appearing (the whole point of the Unpivot).

Deliverable: the frozen schema definitions ready to drop into the CDK Glue table definitions
and the Lambda transform.

## Answer

Frozen against the real data ([findings](../assets/01-data-inspection-findings.md)) and the
Parquet.Net spike ([findings](../assets/02-parquet-net-findings.md)).

### Conventions (both tables)

- All fact column names are **lowercase `snake_case`** (source `UserId`→`user_id`,
  `User_Email`→`user_email`, etc.). The Lambda maps source CSV names → fact names once.
- **`date` and `client_type` are partition keys only** — Hive-style path segments, **not**
  stored in the Parquet body. (Keeping `date` out of the body also sidesteps the Parquet.Net
  INT96 timestamp gotcha.)
- Physical layout:

  ```
  s3://<target-bucket>/usage_daily/date=YYYY-MM-DD/client_type=KIRO_CLI/<key>.parquet
  s3://<target-bucket>/model_messages/date=YYYY-MM-DD/client_type=KIRO_CLI/<key>.parquet
  ```

- **Deterministic output key** = source CSV basename plus a short SHA-256 source-identity
  suffix, e.g. `.../KIRO_CLI_..._202607100000-<hash>.parquet`. The full bucket/key hash avoids
  same-basename collisions; a re-fire overwrites the same object.
- Compression: **Snappy** (Athena's Parquet default).

### `usage_daily` — body columns (grain: date, user_id, client_type)

| Column | Parquet/Glue type |
| --- | --- |
| `user_id` | string |
| `user_email` | string |
| `chat_conversations` | bigint (long) |
| `credits_used` | double |
| `overage_cap` | double |
| `overage_credits_used` | double |
| `overage_enabled` | boolean |
| `subscription_tier` | string |
| `total_messages` | bigint (long) |
| `new_user` | boolean |
| `profile_id` | string |

Partition keys (path, not body): `date` (date), `client_type` (string).

Decisions: credits/overage are **`double`** (not decimal — full-precision source + avoids
Parquet.Net decimal/INT96 gotchas); **`profile_id` kept** (constant ARN, low value but real
and future-proofs multi-profile).

### `model_messages` — body columns (grain: date, user_id, client_type, model)

| Column | Parquet/Glue type |
| --- | --- |
| `user_id` | string |
| `user_email` | string |
| `model` | string |
| `messages` | bigint (long) |

Partition keys (path, not body): `date` (date), `client_type` (string).

Unpivot rules:

- `model` = source column name minus the `_messages` suffix, **verbatim lowercase** (dots
  preserved): `claude_opus_4.8_messages` → `claude_opus_4.8`, `glm_5_messages` → `glm_5`.
- `auto_messages` → `model = "auto"`, an ordinary row (not special-cased; Auto is not a
  guaranteed column).
- **Drop zero-count rows** — emit a row only when `messages > 0`.

### Partition projection (identical on both Glue tables)

- `date`: `projection.date.type = date`, `format = yyyy-MM-dd`, `range = 2026-06-01,NOW`,
  `interval = 1`, `interval.unit = DAYS`.
- `client_type`: `projection.client_type.type = enum`,
  `values = KIRO_CLI,KIRO_IDE,PLUGIN` (PLUGIN included though unseen).
- `storage.location.template` points at each table's S3 prefix with
  `date=${date}/client_type=${client_type}`.

Both schemas are **fixed and stable across new models** (a new model is a new *row* in
`model_messages`, never a new column) — the whole point of the Unpivot.
