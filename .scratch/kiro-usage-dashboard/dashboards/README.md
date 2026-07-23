# Kiro Usage Dashboards

The committed JSON files are the source of truth for both dashboards.

## Automated production provisioning (current)

`KiroInfraStack` includes the `GrafanaProvisioning` custom resource (issue 15): on every
deploy its provider reconciles the permanent `Kiro-Usage` workspace — creates/updates the
`Kiro Usage` folder, upserts and health-checks the Athena data source (`kiro-athena`), and
overwrites both dashboards from these committed files. Stable dashboard UIDs and
`overwrite: true` make each deployment restore the committed definitions; UI drift is
intentionally discarded. **Do not configure the folder, data source, or dashboards through
the Grafana UI.**

The provider uses a short-lived (≤ 15 min) Grafana service-account token that is deleted
after every invocation; no provisioning credential is retained.

## Temporary integration spike (retired)

`KiroGrafanaIntegrationSpikeStack` proved this automation path against an isolated temporary
workspace (issue 14). The spike stack was destroyed on 2026-07-22 after the pattern was
adopted into `KiroInfraStack`; its workflow remains in git history for reference.

## Cap Semantics

The `PRO_MAX` tier cap panels use `max(overage_cap)` = 2500 (from the real data).
The cap-gauge thresholds are green <70% / orange 70–90% / red >90%.
The "Users ≥ 90% Cap" alert panel uses month-to-date aggregation (ignores the dashboard time range).
