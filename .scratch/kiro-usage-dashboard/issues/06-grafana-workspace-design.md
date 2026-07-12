# 06 — Design the Managed Grafana workspace and panels

Type: prototype
Status: resolved
Blocked by: 03

## Question

Design (not stand up) the **Amazon Managed Grafana** dashboard over the Athena facts. Standing
up the workspace is the first post-spec step; here we decide its shape.

Resolve, against the frozen schemas (ticket 03):

- Authentication/authorization for the workspace (IAM Identity Center vs SAML) and who gets
  access — coordinate with ticket 05.
- The Athena data source config (workgroup, catalog/database, results location).
- The panel set: e.g. credits used over time per Target User, total messages/day, messages by
  Model (from `model_messages`), conversations per user, overage consumption, subscription
  tier breakdown. Which use `usage_daily` vs `model_messages`.
- Template variables (User Email, Client Type, date range) driven by partition columns.
- A rough panel layout / mock (linked as an asset) concrete enough to react to.

Deliverable: a dashboard design (panels, queries, variables, auth) ready to build.


## Comments

### Dashboard tooling: Managed Grafana vs QuickSight vs ECS self-host (POC decision)

Evaluated three ways to serve the dashboard. All prices API-sourced (`pricing:GetProducts`)
for **`us-east-1`** (the POC region), captured 2026-07-12 — verify before any scale-up, and
note both QuickSight's and Grafana's per-user pricing change over time.

| Option | Per-user | Fixed floor | Ops burden | Auth |
|---|---|---|---|---|
| **Amazon Managed Grafana** | Editor $9/mo, Viewer $5/mo | none | none (fully managed, HA) | IAM Identity Center built-in |
| **Amazon QuickSight** | Author Pro $40/mo, Reader $3/mo (+SPICE $0.25–0.38/GB-mo) | none | low (managed) | Identity Center / SAML |
| **OSS Grafana on ECS Fargate** | $0 (unlimited users) | ~$29–30/mo (Fargate ~$11 + ALB ~$18 + EFS) | high (patching, TLS, VPC, single-task downtime, own auth) | self-run (Grafana DB / ALB+Cognito / SAML) |

Fargate rates: $0.04048/vCPU-hr + $0.004445/GB-hr. ALB: $0.0225/hr + $0.008/LCU-hr.

**Break-evens (single-author assumption):**
- Managed Grafana vs ECS self-host: ~**4 viewers** ($9 + 4×$5 ≈ $29 flat floor). Below that,
  Managed is cheaper; above, self-host's flat/unlimited model wins if you accept the ops cost.
- QuickSight's $40 author seat makes it the priciest at low scale despite a cheaper $3 reader;
  it only competes at heavy *reader* fan-out, and never beats Grafana for a single author.

**Decision for the POC: Amazon Managed Grafana.** At single-user scale (ticket 01: one
Target User) it's the cheapest (**$9/mo**), zero-ops, and its IAM + IAM Identity Center auth
are already designed in ticket 05. QuickSight and ECS self-host are documented here as the
fallbacks to revisit if (a) viewers climb past ~4–5 (→ reconsider self-host) or (b) native
AWS BI features are needed (→ QuickSight). Revisiting is a scale trigger, not a POC concern.


## Answer

Resolved via `/prototype` (UI branch). Mock:
[assets/06-grafana-dashboard-mock.html](../assets/06-grafana-dashboard-mock.html) — a
throwaway static HTML mock (open in a browser; toggle `?variant=a|b`), grounded in the frozen
schemas (ticket 03) and the real single-user data shape (ticket 01). Verdict after iterating
on the mock: **ship two dashboards — a fleet overview (A) and a single-user drilldown (B)** —
and **add a credits-vs-cap view that leads with a "users over 90%" alert**, full per-user
bars secondary.

> Iteration note: the first mock made A a single-user "ops overview"; on review that didn't
> answer the fleet questions a demo audience asks (who's heavy, who's near cap, tier mix). A
> was reframed to a **fleet overview across all users**; B became the **single-user
> drilldown**. The mock fabricates 5 users so the fleet layout is legible — the real Target
> List still has 1 user today (ticket 01).

### Workspace shape

- One Managed Grafana workspace (auth = **IAM Identity Center**, you = Admin — from ticket 05).
- One folder **`Kiro Usage`** holding **two dashboards** that share the same variables and data
  source (both needed for the demo):
  - **A · Fleet Overview** — fleet KPI row (*active users*, *fleet credits MTD*, *fleet
    messages*, *users near cap*) → **cap alert** (users ≥90%, full sorted bars secondary) →
    per-user leaderboards (*credits by user*, *messages by user*) + *users by tier* → fleet
    trend + *messages by model* + *client-type split*. Answers "how is the whole team using
    Kiro, who's heavy, who's about to run out." Aggregates across all `$user_email`.
  - **B · User Drilldown** — left filter rail (pick one `$user_email`; `$client_type`,
    `$model`; that user's cap gauge) driving a model-forward layout with a per-user/day detail
    table. Answers "what is this specific user doing."

### Data source (from ticket 05)

Athena data source, workgroup **`kiro-usage`**, catalog `AwsDataCatalog`, database
`kiro_usage`, results at `s3://<analytics-bucket>/athena-results/`. Auth via the workspace
role (`AmazonGrafanaAthenaAccess` + scoped inline policy).

### Template variables

| Variable | Type | Source |
|---|---|---|
| `$user_email` | query, multi | `SELECT DISTINCT user_email FROM usage_daily ORDER BY 1` |
| `$client_type` | custom enum, multi | `KIRO_CLI,KIRO_IDE,PLUGIN` (partition; enum avoids a scan) |
| `$model` | query, multi | `SELECT DISTINCT model FROM model_messages ORDER BY 1` |
| time range | built-in | applied to the `date` partition via `$__dateFilter(date)` for partition pruning |

### Panel catalog (panel → fact → query)

Range-scoped panels filter with `$__dateFilter(date) AND user_email IN ($user_email) AND
client_type IN ($client_type)`; `date` is the string partition `YYYY-MM-DD`, parsed to a time
axis with `date_parse(date,'%Y-%m-%d')`.

**Dashboard A · Fleet Overview** (aggregates across all selected users)

1. **Fleet KPI row** (4 stats) — `usage_daily`:
   - Active users = `count(distinct user_email)`
   - Fleet credits (MTD) = `sum(credits_used)` (month-to-date scope, as in panel 4)
   - Fleet messages = `sum(total_messages)` (+ `sum(chat_conversations)` as sublabel)
   - Users near cap = count of users with `sum(credits_used)/max(overage_cap) ≥ 0.9`
2. **Credits by user** (bar) — `usage_daily` `SELECT user_email, sum(credits_used) AS credits ... GROUP BY 1 ORDER BY 2 DESC`
3. **Messages by user** (bar) — `usage_daily` `SELECT user_email, sum(total_messages) ... GROUP BY 1 ORDER BY 2 DESC`
4. **Cap proximity — leads with a "users over 90%" alert** — `usage_daily`. **Month-to-date**,
   so it ignores the dashboard time range and always scopes to the current month:
   ```sql
   SELECT user_email,
          sum(credits_used) AS mtd_credits,
          max(overage_cap)  AS cap,
          sum(credits_used) / max(overage_cap) AS utilisation
   FROM usage_daily
   WHERE date >= date_format(date_trunc('month', current_date), '%Y-%m-%d')
     AND user_email IN ($user_email)
   GROUP BY user_email
   ```
   **Primary treatment:** a *table/alert-list panel* filtered to `utilisation >= 0.9`, sorted
   desc — the "who's about to run out" callout (empty ⇒ a green "no users over 90%" state).
   **Secondary:** a bar-gauge panel of *all* users (value=`mtd_credits`, max=`cap`), thresholds
   **green <70% / orange 70–90% / red >90%**, collapsed/below the alert. In Grafana, either a
   collapsed row or a second panel; the mock uses a `<details>` disclosure.
5. **Users by tier** (bar/piechart) — `usage_daily` `SELECT subscription_tier, count(distinct user_email) ... GROUP BY 1`
6. **Fleet credits over time** (timeseries) — `usage_daily` `SELECT date_parse(date,'%Y-%m-%d') AS "time", sum(credits_used) ... GROUP BY 1 ORDER BY 1` (optionally multi-series `, user_email` + `GROUP BY 1,2`)
7. **Messages by model — fleet** (bar) — `model_messages` `SELECT model, sum(messages) ... AND model IN ($model) GROUP BY 1 ORDER BY 2 DESC`
8. **Client type split** (piechart/bar) — `usage_daily` `SELECT client_type, sum(total_messages) ... GROUP BY 1`

**Dashboard B · User Drilldown** (`$user_email` constrained to one)

9. **Messages/day stacked by model** (timeseries, multi-series) — `model_messages`
   `SELECT date_parse(date,'%Y-%m-%d') AS "time", model, sum(messages) ... GROUP BY 1,2 ORDER BY 1`
10. **Model share** (bar) — `model_messages` (panel 7 scoped to the one user)
11. **Credits vs messages** (timeseries, dual) — `usage_daily` `sum(credits_used)` + `sum(total_messages)` by date
12. **This user's cap gauge** — panel 4's query scoped to the single selected user
13. **Per-user / day detail** (table) — `usage_daily` (+ top-model rollup from `model_messages`):
    `date, user_email, client_type, subscription_tier, credits_used, total_messages, chat_conversations`.

### Notes / deferred

- **Cap semantics caveat:** the cap gauge uses `overage_cap` (2500 in the real data) as the
  ceiling. If PRO_MAX's true monthly *included-credit* allowance differs from `overage_cap`,
  swap the `max(overage_cap)` term for the correct allowance — flagged for the implementer.
- **"New models first seen"** panel (min(date) per model from `model_messages`) was offered
  and **deferred** — not needed for the demo; easy to add later.
- Coordinates with ticket 05 (auth + data-source role, both settled) and consumes ticket 03's
  frozen schemas unchanged.
