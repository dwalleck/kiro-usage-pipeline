# 01 — Inspect the real User Activity Report data

Type: task
Status: resolved
Blocked by: (none)

## Question

Ground the spec in reality: what does the **User Activity Report** data actually look like
in the existing source buckets (account `369434902231`, `us-east-2`,
profile `AdministratorAccess-369434902231`)?

Confirm, from the real objects (not just the docs):

- Which bucket(s) hold the reports, and the exact key prefix/partition layout under
  `.../KiroLogs/user_report/region/year/month/day/00/`.
- Whether data is actually present yet, how far back, and the daily file fan-out
  (per `client_type`, `part_1/part_2…`).
- The real CSV header: static columns vs the dynamic `<model>_messages` columns, exact
  column names/casing, and the `Auto` column.
- Value formats: date format, booleans (`Overage_Enabled`, `New_User`), `Subscription_Tier`
  values, and how empty/zero model columns appear.
- Which/how many distinct `User_Email` values exist (sanity-check the eventual Target List),
  and typical file sizes/row counts.
- Whether the Legacy Analytic Report (`by_user_analytic/`) is also present (informational
  only — it's out of scope for v1).

Deliverable: a short findings note linked from this ticket, capturing the real schema and
layout so tickets 03/04/05 can be resolved against facts.


## Answer

Findings note: [assets/01-data-inspection-findings.md](../assets/01-data-inspection-findings.md)

Inspected the real objects in the source bucket
`kiro-monitoring-activity-report-369434902231` (account `369434902231`).

- **Source bucket / region**: reports live in `kiro-monitoring-activity-report-369434902231`,
  which is in **`us-east-1`** (not us-east-2 as the map states — the Kiro profile is in
  us-east-1; region partition is `us-east-1`). Reconcile with the map.
- **Prefix (confirmed)**:
  `user-activity-reports/AWSLogs/369434902231/KiroLogs/user_report/{region}/{yyyy}/{mm}/{dd}/00/{CLIENT_TYPE}_{accountId}_user_report_{yyyymmddHHMM}.csv`
- **Data present**: 28 CSVs, `2026-06-20`→`2026-07-10` (with gaps). Clients `KIRO_CLI` +
  `KIRO_IDE` only (no `PLUGIN`). No `part_1/part_2` splits; 1 row per file. ~386–445 bytes.
  Three 103-byte UUID marker objects sit directly under `KiroLogs/` — filter to
  `user_report/**/*.csv`.
- **Schema**: 13 fixed static columns exactly as documented
  (`Date,UserId,Client_Type,Chat_Conversations,Credits_Used,Overage_Cap,Overage_Credits_Used,Overage_Enabled,ProfileId,Subscription_Tier,Total_Messages,New_User,User_Email`),
  then dynamic lowercase `<model>_messages` columns (9 distinct combinations seen).
  **`auto_messages` is NOT always present** — it appears only when Auto was used (correction
  to the docs). This is precisely the Unpivot → Model Message Fact case.
- **Value formats**: `Date` ISO `YYYY-MM-DD`; booleans lowercase `false`; `Subscription_Tier`
  = `PRO_MAX` (UPPER_SNAKE, not `ProMax`); `Credits_Used` full-precision float; `UserId`,
  `ProfileId` (full ARN), `User_Email` are double-quoted. Unused models show as column
  absence here (single-user); tolerate `0` cells for multi-user.
- **Distinct users**: exactly **1** — `dwalleck@proton.me`. Single-user personal org, so the
  POC Target List is effectively that one email; multi-user cases handled defensively.
- **Legacy Analytic Report**: present under `by_user_analytic/` (18 files, 46 cols, no email,
  no client type, date `MM-DD-YYYY`). Confirms `CONTEXT.md`; **out of scope for v1**.

Status: resolved.
