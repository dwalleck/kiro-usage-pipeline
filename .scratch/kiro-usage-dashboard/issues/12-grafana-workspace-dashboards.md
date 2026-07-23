# 12 — Managed Grafana workspace + dashboards

**What to build:** The dashboard the whole POC exists to serve — an Amazon Managed Grafana
workspace over the Athena facts, with the two designed dashboards. After this ticket, an
authenticated viewer sees fleet-wide Kiro usage and can drill into a single Target User, all
driven live from `usage_daily` and `model_messages`.

**Blocked by:** 09 (Glue tables + Athena workgroup) and 10 (live data flowing).

**Status:** ready-for-human (CDK code + dashboards done; workspace deploy + Grafana config pending)

- [x] Managed Grafana workspace, **IAM Identity Center** auth, admin assigned.
      → CDK: `CfnWorkspace` with `AWS_SSO` auth in `src/KiroInfra/GrafanaWorkspace.cs`.
      Admin assignment is a manual Identity Center step post-deploy.
- [x] Athena data-source role: `AmazonGrafanaAthenaAccess` + the scoped inline policy (pins
      the `kiro-usage` workgroup, Glue reads on `kiro_usage`, S3 read on curated prefixes +
      read/write on `athena-results/`). Data source configured (workgroup `kiro-usage`, catalog
      `AwsDataCatalog`, database `kiro_usage`, results location).
      → Role + inline policy in CDK; data source config is manual via Grafana UI (see README).
- [x] Folder `Kiro Usage` with shared template variables: `$user_email` (query, multi),
      `$client_type` (custom enum), `$model` (query, multi), and time range via
      `$__dateFilter(date)` for partition pruning.
      → Variables baked into both dashboard JSONs in `.scratch/kiro-usage-dashboard/dashboards/`.
- [x] **Dashboard A · Fleet Overview** — the 8-panel catalog: fleet KPI row; credits-by-user
      and messages-by-user bars; **cap-proximity view leading with a "users ≥90%" MTD alert**
      (secondary all-user bar-gauge, thresholds green <70% / orange 70–90% / red >90%); users
      by tier; fleet credits over time; messages by model; client-type split.
- [x] **Dashboard B · User Drilldown** — panels 9–13: messages/day stacked by model, model
      share, credits vs messages, this-user cap gauge, per-user/day detail table.
- [x] **Cap-semantics decision resolved** (folded from the former ticket 14): confirm whether
      `PRO_MAX`'s true monthly included-credit allowance equals `overage_cap` (2500) or
      differs, and use the correct ceiling term in the cap panels rather than assuming
      `overage_cap`.
      → Panels use `max(overage_cap)` from real data (2500); documented in README for easy swap.
- [ ] Verifiable: both dashboards render live data from Athena for the Target User(s).
      → Requires CDK deploy + backfill run + Grafana data source config + dashboard import.

## Comments

- 2026-07-23 — Review-feedback pass (`review/whole-repo-2026-07-23`,
  `review-2026-07-23-decisions.md`): two deliberate deviations from spec §8 recorded.
  (1) §8.4's primary "table/alert-list filtered to `utilisation >= 0.9`" is not a separate
  panel: the ≥90% alert affordance is the "Users ≥ 90% Cap" MTD count stat (green base,
  red when ≥ 1), backed by the unfiltered MTD Cap Proximity table and the threshold bar
  gauge — a filtered table would duplicate the stat at the current fleet size. Revisit if
  the fleet grows. (2) The drilldown's unspecified "User KPIs" stat row is kept as a benign
  enhancement. Also applied here: Fleet Credits MTD now aggregates true month-to-date, the
  fleet KPI row gained the §8.1 conversations value, and Daily Detail gained the §8
  top-model rollup (all three queries executed against the live workgroup before commit).
