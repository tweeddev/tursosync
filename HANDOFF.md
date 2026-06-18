# TursoSync — handoff

_Snapshot: 2026-06-17. Use this to resume work in a fresh chat._

## What this is

**TursoSync** is a **drop-in ADO.NET provider for [Turso](https://github.com/tursodatabase/turso)**
(namespace `Turso`) with **local↔cloud sync** + **tantivy FTS** — the pieces the official `Turso.Data`
binding has in source but never publishes. There is **no official Turso package on nuget.org** (their .NET
binding is `IsPackable=false`; the community ones are libsql-based), so TursoSync is first-of-kind: the real
Rust engine + sync + FTS with bundled natives. It was ported from `tursodatabase/turso`'s Go sync binding
(`bindings/go/driver_sync.go` → `TursoSyncDatabase`'s host-driven IO loop).

## This repo

- **github.com/tweeddev/tursosync** (public), `main @ 7d843f1`. Local clone for `rig`/`shiprig`: this dir.
- **Downstream consumer:** the Tweed app (`github.com/JohnCampionJr/tweed`, private) uses these packages via
  NuGet from `Tweed.Store` — a real-world consumer + the original home (the code was extracted from there).

## Published

`TursoSync`, `TursoSync.DbUp`, `TursoSync.Dapper` — all at **`0.1.0-preview.1`** on nuget.org (multi-target
net8/9/10; `TursoSync` bundles all six RID natives under `runtimes/<rid>/native/`, ~40 MB).

## Package family

- **TursoSync** — ADO.NET provider (`TursoConnection`/`Command`/`Reader`/`Parameter`/`Transaction`,
  `TursoFactory`), the **sync engine** (`TursoSyncDatabase`), UDFs/aggregates/collations/load-extension,
  local at-rest encryption, connection pooling. Namespace `Turso` (drop-in for official `Turso.Data`).
- **TursoSync.DbUp** — `DeployChanges.To.TursoDatabase(...)` (namespace `DbUp` / `DbUp.Turso`).
- **TursoSync.Dapper** — `TursoTypeHandlers.Register()` (Ulid/DateTimeOffset/Guid → TEXT).
- Parity map: `TURSO-PARITY.md` (feature-complete vs official `Turso.Data`+`Turso.Raw`).

## Key architecture decisions

- **Two lanes, one API:** no `Remote Url` → base engine (`AsyncIO=0`, no IO pump); remote (or `Sync=true`) →
  sync engine. In **release** builds local Turso ≈ SQLite; **debug natives are ~25× slower** (release is
  mandatory for shipped natives).
- **One cdylib, both layers:** `turso_sync_sdk_kit` exports base `turso_*` + `turso_sync_*` + tantivy FTS.
  Built `--features turso_sdk_kit/fts --release --config profile.release.strip=true` (strip keeps the package
  under nuget.org's **250 MB** cap — Linux debuginfo was ~120 MB → ~16 MB stripped).
- **Native resolution:** packaged natives resolve via `runtimes/<rid>/native` (NuGet deps.json). Dev override:
  env var **`TURSOSYNC_NATIVE_DIR`** → a folder holding the built native. `TursoNativeLibrary` also walks up
  for a `reference/turso-main/target/...` checkout as a dev convenience.
- **Engine pin:** `turso-engine.json` (tag + commit SHA) is the **source of truth**; CI/release read it.
  Currently `v0.7.0-pre.8` → `bfcf68f992479b3deb946da5baf2a9b17937463a`.

## CI / release (`.github/workflows/`)

- **`ci.yml`** — build+test on 3 OS; fetches the engine at the pinned SHA, builds the native (release+FTS),
  runs the suite.
- **`release.yml`** — triggered by shipRig's tag **`TursoSync@*`**; matrix builds all 6 RID natives on
  **native runners** (incl. `ubuntu-24.04-arm`, `windows-11-arm`; osx-x64 cross-built on Apple Silicon since
  `macos-13` Intel is being retired) → strip → pack → **NuGet trusted publishing (OIDC, keyless)** → creates
  a **GitHub Release** for the tag (auto-generated notes, prerelease-aware, attaches the .nupkgs).
- **`engine-bump.yml`** — weekly; opens a PR bumping `turso-engine.json` when the upstream series has a newer
  release; CI validates the new ABI on the PR before merge.
- **Repo config:** variable **`NUGET_USER=JohnCampionJr`**; a NuGet **trusted-publishing policy** (account →
  Trusted Publishing; owner/repo/workflow must match the run exactly). No `NUGET_API_KEY` needed.

## Tooling & release cycle

- **rig** (`.rig.json`) = dev loop: `rig build` / `rig test` / `rig coverage`, plus `rig engine <tag|latest>`
  (pin engine) and `rig pack` (Tier-0 local build+pack+consume test via `./local-pack.sh`).
  - Local test/coverage needs the native: set `TURSOSYNC_NATIVE_DIR`, or `TURSO_SRC` (a turso checkout) so
    `local-pack.sh`/`scripts/bump-turso.sh` can build it.
- **shiprig** (`.changeset/`) = releases. `config.json`: `fixed:[TursoSync,.DbUp,.Dapper]` (ship together),
  `ignore:[TursoSync.Tests]`. `release.jsonc` order = **version → commit → tag → push** (build+publish
  delegated to CI — the local machine can't cross-build the 6 natives, and we publish via OIDC).
- **Cut a release:** `shiprig add` (record change+bump) → `shiprig release` → tags `TursoSync@x.y.z` + pushes
  → `release.yml` builds+publishes. For previews: `shiprig pre enter preview` first (a plain patch/minor from
  `-preview.1` resolves to `0.1.0`, dropping the prerelease).

## Coverage

**34 tests** (31 + 3 live-sync). Well-covered: factory/config/params/marshal/raw-connection. The
**remote-sync path** (Push/Pull/Stats/Checkpoint + HTTP IO handler) is now exercised for real by
`LiveSyncIntegrationTests` — they spawn a self-hosted `tursodb --sync-server` (gated on
**`TURSOSYNC_SYNC_SERVER`** → a `tursodb` binary; CI builds it and sets the var, so they run on all 3 OSes;
locally `cargo build -p turso_cli --release` then export the path). The external-cloud round-trip
(`PushPull_RoundTripsThroughRemote`, gated on `TWEED_TURSO_SYNC_URL`) remains for validating real Turso Cloud.
Remaining gaps: error/edge branches in Command/Reader/Pool/Extensions; trivial ctors in TursoException.

## Open items / next steps

1. **Version decision:** recommend cutting **`0.1.0`** (drop `-preview`) as the first real release. **Hold
   `1.0.0`** until: the Turso **engine reaches a stable (non-pre) release**, the **remote-sync path has
   coverage**, and **sync-lane at-rest encryption** is validated (known gap; base-lane encryption works).
2. ~~**Biggest test gap:** add a gated live-sync integration test.~~ **Done** — `LiveSyncIntegrationTests`
   spawns `tursodb --sync-server` and round-trips Push/Pull/Stats/Checkpoint; CI builds the server and runs
   them for real on all 3 OSes. (Sync-lane at-rest encryption is still unvalidated — see item 1.)
3. **engine-bump PRs:** to get CI to run on the bot PR, add repo secret **`BUMP_PAT`** + enable "Allow GitHub
   Actions to create and approve pull requests."

_Done: nuget API key rotated; GitHub Release step added to `release.yml`; Tweed swapped to the published
packages; live-sync integration tests added (open item #2)._

## Gotchas learned

- nuget.org **250 MB** package cap → strip natives (done).
- **macos-13 Intel runners are fading** → osx-x64 is cross-built on Apple Silicon (reliable Mac cross-arch).
- ARM Linux/Windows runners are **free only on public repos** (this repo is public).
- MSTest 4 / Microsoft.Testing.Platform: a test csproj inheriting multi-`TargetFrameworks` reports nothing
  under `dotnet test` — pin the test project to a single `TargetFrameworks` (net10).
- Trusted publishing: the policy is **owner+repo+workflow, no package name**; the `user` must be the policy
  **creator's exact username**, and the repo/workflow fields must match the run's OIDC claims exactly (a
  repo-name typo was the only thing that broke the first release).
