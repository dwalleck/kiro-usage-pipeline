# 01 — Data Inspection Findings: real User Activity Report data

Grounding facts for the Kiro Usage Dashboard POC spec, gathered by inspecting the **real**
objects in the source buckets. Account `369434902231`, profile
`AdministratorAccess-369434902231`. Inspected via S3 list + GET (no Prompt Log content read).

> Note on region: the map/tickets say `us-east-2`, but the **actual** source bucket and its
> `region` partition are **`us-east-1`** (this is where the Kiro profile was installed —
> the ProfileId ARN is `...codewhisperer:us-east-1:...`). Tickets 03/04/05 should target
> `us-east-1` for the source, or confirm the intended source region explicitly.

## 1. Buckets

- **User Activity Report source**: `kiro-monitoring-activity-report-369434902231`
  (LocationConstraint resolves to **us-east-1**).
- **Prompt Logs** (out of scope, content NOT read): `kiro-monitoring-prompt-logs-369434902231`.
- No other kiro/activity/user_report-named buckets.

## 2. Key layout (confirmed against real objects)

Prefix root: `user-activity-reports/` (the configured prefix), then the Kiro-managed path:

```
user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/{region}/{yyyy}/{mm}/{dd}/00/{CLIENT_TYPE}_{accountId}_user_report_{yyyymmddHHMM}.csv
```

Concrete example:

```
user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/us-east-1/2026/07/10/00/KIRO_CLI_369434902231_user_report_202607100000.csv
```

Matches the documented `.../KiroLogs/user_report/region/year/month/day/00/` shape, with the
real prefix `user-activity-reports/` and region `us-east-1`. The `00` segment is the fixed
02:00-UTC hour partition.

### Data presence, range, fan-out
- **Data is present.** `user_report/` holds **28 CSV objects**.
- **Date range**: `2026-06-20` → `2026-07-10` (some gaps, e.g. 06-27/06-28 missing).
- **Client types seen**: `KIRO_CLI` (nearly every day) and `KIRO_IDE` (subset of days).
  **No `PLUGIN` files** were produced (plugin not used).
- **No `part_1/part_2` splits** — this org has a single user, far below the 1,000-user
  split threshold. Each file has exactly **1 data row**.
- File **sizes**: ~386–445 bytes. **Row count**: 1 per file (single user).
- **Stray objects**: three 103-byte objects with UUID names sit directly under
  `.../KiroLogs/` (e.g. `.../KiroLogs/4cc505fb-...`). These look like S3 delivery
  permission-check / test markers, not reports. A consumer should key strictly off the
  `user_report/` prefix and the `*.csv` suffix to avoid picking them up.

## 3. Real CSV schema — User Activity Report

### Static columns (fixed, in this exact order and casing)
```
Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used,Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email
```
This matches `per-user-activity.txt`. These 13 columns map cleanly onto **Daily Usage Fact**
(`usage_daily`) at (Date, User Id, Client Type) grain.

### Dynamic model columns (`<model>_messages`)
- Appended **after** `User_Email`.
- Named **all-lowercase**, `{model}_messages`, in **alphabetical order**, with
  **`auto_messages` sorting first** when present.
- **Present only when that model was used that day** — they are NOT a fixed set.
  Across the 28 files there were **9 distinct model-column combinations**. Full set of
  model columns observed:
  - `auto_messages`
  - `claude_haiku_4.5_messages`
  - `claude_opus_4.6_messages`
  - `claude_opus_4.8_messages`
  - `claude_sonnet_5_messages`
  - `glm_5_messages`
- Model names embed dots and underscores (`claude_opus_4.8`, `claude_haiku_4.5`, `glm_5`).
- This is exactly the case the **Unpivot** step exists for → **Model Message Fact**
  (`model_messages`), one row per (Date, User Id, Client Type, Model). A new model is a new
  *row*, never a new column.

> **Correction to the docs:** the reference says model columns always start with an `Auto`
> column. In reality **`auto_messages` appears only on days Auto was used** (present in most
> KIRO_IDE files, absent from many KIRO_CLI files). Treat `auto_messages` as just another
> dynamic model column (that happens to sort first), NOT a guaranteed column. Do not assume
> any particular model column is always present.

### Two real example rows (2026-07-10)
KIRO_CLI (`...claude_haiku_4.5_messages,claude_opus_4.8_messages`):
```
2026-07-10,"215bb5b0-00a1-70cd-1caf-57794fdc8915",KIRO_CLI,8,114.45787414391377,2500.0,0.0,false,"arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU",PRO_MAX,131,false,"dwalleck@proton.me",8,123
```
KIRO_IDE (`...auto_messages`):
```
2026-07-10,"215bb5b0-00a1-70cd-1caf-57794fdc8915",KIRO_IDE,2,0.7132330391376451,2500.0,0.0,false,"arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU",PRO_MAX,4,false,"dwalleck@proton.me",4
```

## 4. Value formats (observed)

| Column | Format observed | Notes |
|---|---|---|
| `Date` | `2026-07-10` | **ISO `YYYY-MM-DD`** (not the legacy MM-DD-YYYY) |
| `UserId` | `"215bb5b0-00a1-70cd-1caf-57794fdc8915"` | **quoted** UUID = the bare **User Id** |
| `Client_Type` | `KIRO_CLI` / `KIRO_IDE` | unquoted; `PLUGIN` possible but not seen |
| `Chat_Conversations` | `8` | integer |
| `Credits_Used` | `114.45787414391377` | **float, full precision** (many decimals) |
| `Overage_Cap` | `2500.0` | float; preset plan max when overage disabled |
| `Overage_Credits_Used` | `0.0` | float |
| `Overage_Enabled` | `false` | **lowercase** boolean literal |
| `ProfileId` | `"arn:aws:codewhisperer:us-east-1:369434902231:profile/UV4C4VUDDGRU"` | quoted full ARN (not a bare id) |
| `Subscription_Tier` | `PRO_MAX` | **UPPER_SNAKE**, not the doc's `ProMax`. Only value seen |
| `Total_Messages` | `131` | integer |
| `New_User` | `false` | **lowercase** boolean literal |
| `User_Email` | `"dwalleck@proton.me"` | **quoted** |
| `<model>_messages` | `8`, `123`, `4` | integer message counts |

Quoting rule observed: string fields (`UserId`, `ProfileId`, `User_Email`) are
double-quoted; numerics, booleans, and `Client_Type` are unquoted. A parser must handle
quoted fields (the ProfileId ARN and any commas safely).

### Zero / empty model columns
- **Not observed as `0` in this dataset.** Because these are single-user files, only the
  models actually used appear as columns; an unused model is represented by **column
  absence**, not a `0` cell.
- The docs state that when a model column *is* present but unused it shows `0` — that would
  occur in multi-user files (one user used the model, another didn't). We couldn't observe
  it here (1 user), so the pipeline should still **tolerate `0`-valued model cells** and
  ideally drop zero rows during Unpivot.

## 5. Distinct users / Target List sanity check
- **Exactly 1 distinct `User_Email`: `dwalleck@proton.me`** across all 28 files.
- Only tier seen: `PRO_MAX`. `Overage_Enabled` and `New_User` were `false` throughout.
- Implication: this is a **single-user personal org**. The **Target List** for the POC will
  effectively be `["dwalleck@proton.me"]`. Multi-user behavior (part splits, `0` model
  cells, multiple tiers) can't be exercised from this data and must be handled defensively.

## 6. Legacy Analytic Report (informational — out of scope for v1)
- **Present.** Path:
  `.../KiroLogs/by_user_analytic/{region}/{yyyy}/{mm}/{dd}/00/{accountId}_by_user_analytic_{ts}_report.csv`
- **18 objects**, ~1466 bytes each, 1 row each.
- Header starts `UserId,Date,Chat_AICodeLines,...` — **46 columns**, detailed
  code-acceptance metrics.
- Confirms `CONTEXT.md`: **no `User_Email`, no `Client_Type`**, and date is
  **`07-10-2026` (MM-DD-YYYY)**. Cannot be filtered by the Target List without a
  User Id→email bridge. Remains **out of scope** for v1.

## 7. Takeaways for tickets 03/04/05
- **03 (fact schemas)**: `usage_daily` = the 13 fixed static columns; `model_messages` =
  Unpivot of the dynamic `<model>_messages` columns → (Date, User Id, Client Type, Model,
  messages). Both carry `User_Email` as a label. Types: `Date`=date, ids/tier/email=string,
  `Credits_Used`/`Overage_*`=double, counts=long/int, booleans=bool from `true`/`false`.
- **04 (backfill)**: enumerate objects under
  `user_report/{region}/{yyyy}/{mm}/{dd}/00/`, filter to `*.csv`, ignore stray non-CSV
  marker objects; data currently spans 2026-06-20 → 2026-07-10.
- **05 (IAM)**: read scope is bucket `kiro-monitoring-activity-report-369434902231`
  (us-east-1), prefix `user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/*`.
- **Region caveat**: source is **us-east-1**, not us-east-2 — reconcile with the map.
