# Review-feedback decisions — whole-repo review @ e75ba64 (2026-07-23)

Two-axis review (Standards / Spec) of the full tree, followed by per-finding
verification per `assessing-review-feedback`: every bug claim was reproduced (or
refuted) against the code before any fix was applied. Fixes land as one commit per
finding (or per shared root cause) on `review/whole-repo-2026-07-23`.

Verification tooling: TUnit suite (78 tests), `cdk synth` + template inspection for
IAM claims, and live Athena execution (`kiro-usage` workgroup) for every changed
dashboard query — dashboard SQL is never shipped unexecuted (the 2026-07-22
"No data" incident was malformed panel SQL).

## Standards axis

| # | Finding (one line) | Category | Verified? | Decision | Note |
|---|---|---|---|---|---|
| H1 | README §8/§9 teach manual Grafana setup the dashboards README forbids | Docs bug | Yes (README 339–365 vs dashboards README "Do not configure … through the Grafana UI") | Accept | Replaced with a verification step; `docs(readme)` commit |
| H2 | README still says spike "not yet the production authorization model"; provisioner labeled Temporary | Docs bug | Yes (contradicts d81e111 adoption) | Accept | Same commit as H1 |
| H3 | Program.cs comment claims a Target List cache exists | Docs bug | Yes (no cache; `ProcessCsvAsync_TwoInvocations_RefreshesTargetListEachTime` pins per-invocation reads; spec §4 wants edit-without-redeploy) | Modify | Fixed the comment, not the code — re-reading SSM is by design |
| H4 | AGENTS.md build list omits KiroGrafanaProvisioner | Docs bug | Yes | Accept | `docs(agents)` commit |
| H5 | GrafanaWorkspace.cs comment says dashboards are manually imported | Docs bug | Yes (stale since issue 15) | Accept | Same commit as H3 |
| S1 | Production provisioner still speaks "spike" (service-account name, fallback physical ID, commit message, comments) | Style | Yes; rename risk checked — success responses return WorkspaceId as physical ID and Delete is a no-op, so renames are operationally inert | Accept | `refactor(grafana): retire spike-era naming` |
| S2 | Duplicated code: dashboard UIDs in construct + handler; Athena plugin-id literal beside its const; truncated-listing guard twice in IngestService | Style | Yes (all three sites) | Accept | UIDs now flow as resource properties (single source: the construct); literal uses `AthenaPluginId`; guard extracted as `RequireContinuationToken` |
| S3 | Data clump: three Grafana group IDs travel as loose strings | Style (judgement) | Yes, but both endpoints are inherently flat (cdk.json context keys; CFN resource properties) | Reject | A bundling record would only decorate the middle hop; removes no failure mode. Revisit if a third consumer appears |
| S4 | Primitive obsession: sequencer hex validated in ctor and in CompareSequencers | Style (judgement) | Yes, but the two checks guard different trust boundaries (S3 event input vs persisted state file) | Reject | Not redundant validation; a Sequencer value type is wrong-sized for a 2-site invariant. Revisit on a third site |
| S5 | GrafanaWorkspace.FolderName is dead | Style | Yes (zero references) | Accept | Deleted |
| S6 | ProcessCsv lacks the Async suffix its siblings use | Style | Yes | Accept | Renamed to ProcessCsvAsync (interface, service, handler, tests) |
| S7 | KiroInfra.csproj lacks Nullable/AnalysisLevel set by both sibling projects | Style | Yes | Accept | Enabled both; fixed 6 nullable annotations; CA1806 suppressed with the repo's existing constructs rationale; CA1711/CA1861 NoWarn'd with justification |

## Spec axis

| # | Finding (one line) | Category | Verified? | Decision | Note |
|---|---|---|---|---|---|
| P1 | Fleet KPI row missing the "(+ conversations)" value (spec §8.1) | Bug | Yes (no panel read chat_conversations) | Accept | Second query on the Fleet Messages stat; Athena-validated |
| P2 | Daily Detail lacks the top-model rollup (spec §8, panel 13) | Bug | Yes | Accept | row_number() LEFT JOIN per (date, client_type, user_id); Athena-validated with real data |
| P3 | Cap-proximity primary "table filtered to utilisation ≥ 0.9" not implemented (spec §8.4) | Design | Partly — §8.1's ≥90% count KPI exists (panel 5, HAVING ≥ 0.9, green→red thresholds) and §8.4's secondary all-user gauge exists (panel 9) | Reject (documented deviation) | The count stat is the alert affordance; a filtered table duplicates it at one-user fleet size. Recorded in issue 12 Comments — not a deferral, a decision |
| P4 | Lambda gets unconditioned s3:List* instead of spec §7.3's prefix-scoped ListBucket | Bug | Yes — GrantRead/GrantReadWrite put s3:List* + s3:GetBucket* on both bucket ARNs (worse than reported) | Modify | Hand-rolled statements: version-aware GetObject, prefix-conditioned ListBucket on both buckets, object CRUD on the three analytics prefixes, explicit KMS grants for the UseCustomKey path. Verified in the synthesized template |
| P5 | Issue 15's "update README.md" scope not completed | Bug | Yes (duplicate of Standards H1/H2 — same root cause) | Accept | Absorbed into the H1/H2 README commit; noted in issue 15's own scope |
| P6 | Backfill-only ValidateExpectedDate makes backfill stricter than live (spec §6: backfill is the live path in a loop) | Bug | Yes (live never sets ExpectedDate; a path/body date mismatch would poison every backfill page while live ingests it) | Modify | Body dates are authoritative; demoted to a structured `ingest_date_mismatch` warning computed identically on both paths; ExpectedDate plumbing removed; both paths covered by new tests |
| P7 | ReportTransform throws on duplicate grain; spec §10 said "handle defensively" | Design | Yes (throw confirmed at ReportTransform.cs:104) | Reject | Emitting duplicate grain rows double-counts in Athena; first/last-wins is silent corruption. Throw → DLQ + alarm is the defensive handling; spec §10's defensive list (0-cells, part_N) never covers duplicate grain |
| P8 | Drilldown gained an unspecified 4-stat "User KPIs" row | Scope creep | Yes | Reject (keep) | Benign, consistent with the dashboard's purpose; recorded in issue 12 Comments |
| P9 | "Fleet Credits MTD" stat reports the dashboard range, not month-to-date (spec §8.1) | Bug | Yes ($__dateFilter vs the MTD predicate panels 5/8/9 already use) | Accept | Reused the proven `date >= CAST(date_trunc('month', current_date) AS date)` predicate; Athena-validated. Note: the spec's own §8.4 SQL is string-typed and would fail — the Glue `date` partition column is typed `date` (QueryLayer.cs:100) |

## Tally

21 findings → 11 accepted, 3 modified (fix differed from the reviewer's framing),
5 rejected with rationale, 2 absorbed as duplicates of the same root cause
(P5→H1/H2). No deferrals, so no new tracker issues were required — every reject is
a terminal decision recorded here, not a silent drop.
