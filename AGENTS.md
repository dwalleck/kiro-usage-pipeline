# AGENTS.md

## Agent skills

### Issue tracker

Issues live as markdown files under `.scratch/<feature>/` (local-markdown, no remote). See `docs/agents/issue-tracker.md`.

### Triage labels

Default canonical roles: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` + `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Build & test

- **CDK infrastructure** (KiroInfra): `dotnet build src/KiroInfra.sln` — requires .NET 8 SDK.
- **Ingest Lambda** (KiroIngest): `dotnet build src/KiroIngest/KiroIngest.csproj` — requires .NET 10 SDK.
- **Tests** (KiroIngest.Tests): `dotnet run --project test/KiroIngest.Tests/KiroIngest.Tests.csproj` — TUnit on Microsoft.Testing.Platform. Do **not** use `dotnet test`; it is incompatible with MTP on .NET 10 SDK.
- **CDK synth/deploy**: `npx cdk synth --profile AdministratorAccess-369434902231 --strict` (requires Docker running for Lambda bundling).
