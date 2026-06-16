# CLAUDE.md - Development Context for Skyline Cadenza

This file orients Claude Code (and other coding agents) to the Skyline Cadenza repository. It complements the user's global `~/.claude/CLAUDE.md`, which still applies; this file documents only what is repo-specific.

## Project Overview

Skyline Cadenza is a Skyline external tool (WPF, .NET 8) that designs load-balanced PRM / MTM targeted proteomics assays from DIA-NN GPF reports or Carafe AI-predicted spectral libraries. It implements the algorithm described in the Stellar MS Webinar 2024 ("Balancing the Load") and US 11,688,595 B2 (Remes & MacCoss, 2023).

The tool runs as a self-contained zip installed via Skyline's `Tools > External Tools > Add > File...` dialog. At launch Skyline passes a JSON-RPC pipe name as `args[0]`; Cadenza connects to the active document and pushes scheduled targets back via `SkylineCmd --import-transition-list`.

Lineage: ported from the prototype notebook at [`maccoss/targeted-modeling`](https://github.com/maccoss/targeted-modeling) (`gpf_coverage.ipynb`).

## Architecture

Three projects under `src/`:

- **`SkylineCadenza.Core`** (`net8.0-windows`) — pure algorithms + I/O + RPC client. No WPF references. Contains parsimony, the scheduler, DIA-NN / Carafe ingest, Skyline-document ingest, the Thermo CSV writer, and link-compiled copies of pwiz's `SkylineTool` client sources. Should remain UI-free so it can be tested headlessly.
- **`SkylineCadenza.App`** (`net8.0-windows`, WPF) — the `.exe` Skyline launches. Contains `MainViewModel`, the XAML, per-plot rendering code, and the `tool-inf` manifest pair.
- **`SkylineCadenza.Tests`** (xUnit, `net8.0-windows`) — 21 tests today, including golden-file regressions against the notebook prototype. New algorithm changes should include or update a golden fixture.

Vendored pwiz sources live in `external/SkylineTool/` and are link-compiled into `Core` via `<Compile Include="..\..\external\SkylineTool\..." Link="..."/>` — same pattern `SkylineMcpServer.csproj` uses upstream. Do not modify these files; if pwiz upstream changes, re-vendor wholesale.

## Build Commands

```bash
# Restore + build
dotnet restore SkylineCadenza.sln
dotnet build SkylineCadenza.sln               # Debug
dotnet build SkylineCadenza.sln -c Release    # Release

# Tests (always run before committing)
dotnet test SkylineCadenza.sln

# Package the Skyline-ready zip
dotnet msbuild build/package.proj             # outputs publish/SkylineCadenza.zip

# Local smoke install (from Windows PowerShell)
./tools/install-dev.ps1                       # publishes into %LOCALAPPDATA%\Apps\SkylineDaily\Tools\SkylineCadenza\
```

`Directory.Build.props` sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>` so all of the above also work on WSL / Linux (CI uses `windows-latest` for authenticity, but the cross-compile path is supported for local iteration).

## CI

- `.github/workflows/ci.yml` — runs on push to `main` / `develop` and PRs to `main`. Restores, builds Debug, runs xUnit, builds Release, packages the zip, uploads as a workflow artifact.
- `.github/workflows/release.yml` — triggers on `v*` tags. Verifies that `release-notes/RELEASE_NOTES_<tag>.md` exists and that `Version` in `info.properties` matches the tag before doing anything destructive, then builds and publishes a GitHub Release with `SkylineCadenza-<tag>.zip` attached.

If a release fails because of the verification check, the fix is to bump the version file or rename the notes file, then re-tag (delete the failed tag, push the new one) — never bypass the check.

## Key Files

- `src/SkylineCadenza.Core/Scheduling/Scheduler.cs` — the greedy two-pass scheduler. Cover pass + load-up pass; both implement the published RT-budget rule. `RtAwareCoverSelection` (default on) toggles between proactive (look-ahead) and reactive (legacy / webinar-literal) cover-pass peptide selection.
- `src/SkylineCadenza.Core/Scheduling/SchedulingParameters.cs` — every user-tunable knob. Adding a knob means updating this record + a `MainViewModel` `[ObservableProperty]` + the XAML + a `partial void On...Changed` hook.
- `src/SkylineCadenza.Core/Ingest/SkylineLibraryLoader.cs` — Load-from-running-document path. RT column probe (six candidates, requires non-zero values in 200-row sample) + user override. BLIB-first firing window: `SkylineBlibDiscovery` enumerates active libraries via `GetSettingsListSelectedItems("Libraries")`, `BlibRetentionTimeReader` aggregates `MIN(startTime)` / `MAX(endTime)` from the per-replicate `RetentionTimes` table (no `bestSpectrum` filter — we want the union, not the canonical row), and per-peptide priority across multiple BLIBs is set by variance score (measured DIA-NN / PRM libraries naturally outrank single-prediction Carafe / Prosit libraries). Per-replicate score column is filtered against the user's `QValueCutoff` according to `ScoreTypes.probabilityType` semantics. Falls back to `RT ± peakHalfWidthMin` for peptides not in any BLIB or for documents whose libraries are not BiblioSpec.
- `src/SkylineCadenza.Core/Ingest/BlibRetentionTimeReader.cs` — Read-only SQLite reader for `.blib` files. Returns per-(modSeq, charge) firing windows plus a variance score. Used by `SkylineLibraryLoader` and tested against `testdata/sample.blib`.
- `src/SkylineCadenza.Core/Output/BlibAssayWriter.cs` — Writes a self-contained BiblioSpec nr `.blib` representing the scheduled assay. One `RefSpectra` row per (peptide, charge) with top-6 fragments (intensity-descending → m/z-sorted, zlib-BLOB encoded), per-replicate `RetentionTimes` row mirroring the firing window. Replaces the old `--import-transition-list` CSV path: pushing to Skyline now writes a BLIB + registers it via `AddSettingsListItem("Libraries", xml)`. The user adds peptides through Skyline's Library Explorer so the document's transition filter (ion charges, ion types) drives fragment generation rather than Cadenza asserting fragment-level charges in a transition list.
- `src/SkylineCadenza.Core/SkylineRpc/SkylineSession.cs` — connect-per-call pattern. Every `Execute` opens a fresh pipe.
- `src/SkylineCadenza.App/MainWindow.xaml.cs` — all plot rendering. Uses ScottPlot 5; `HeatmapPlot.Reset()` is required between renders or color bars accumulate. The slot-occupancy plot now does a full histogram with max/median/mode in the title.

## Critical Invariants

- **Skyline RPC must use the `SkylineMcpJson-` pipe prefix**, not the legacy ToolService pipe. `SkylineSession.FromArguments()` wraps `args[0]` correctly; never connect to `args[0]` directly.
- **Connect-per-call**, never connect-once. Holding the pipe open across calls produces `JsonReaderException: 0x00 is invalid start of value` after idle.
- **Push-to-Skyline is a four-stage sequence, all via JSON-RPC.** (1) `BlibAssayWriter.Write` produces a self-contained `.blib`. (2) `AddSettingsListItem("Libraries", xml, overwrite: true)` registers it, followed by `SelectSettingsListItems("Libraries", union)` so it is selected in the document, not just present in the list. (3) `SkylineSettingsConfigurator.ConfigureAsync` aligns the live document's peptide and transition filter with the assay (precursor / product ion charge unions, ion types, ion range, library-pick top-N, peptide length bounds) via `RunCommand` with `--peptide-*` / `--tran-*` flags. (4) Transition-list CSV import via `RunCommand --import-transition-list` populates the target tree; fragment rows now carry their real `Product Charge` from `FragmentIon.Charge`.
- **`RunCommand` via JSON-RPC operates on the LIVE document, not a saved file.** Per Nick Shulman: calling `RunCommand` against the JSON-RPC pipe is equivalent to typing those `SkylineCmd`-style commands into Skyline's Immediate Window (View > Other Windows > Immediate Window). No `--in` / `--out` round-trip is needed. This is what lets `SkylineSettingsConfigurator` mutate transition filter settings on the running document. Flags that the installed Skyline build doesn't recognise come back as text in the `RunCommand` return value rather than throwing, so the configurator captures and surfaces those without aborting the rest of the push.
- **The cycle budget is per-RT-bin, not global.** A peptide consumes one slot only in the bins its padded firing window touches. A protein with peptides spread across the gradient does not block more total budget than 5 different proteins with 1 peptide each at distinct RTs.
- **BLIB-sourced RT boundaries already include per-replicate peak-shape variance.** `FiringPadSec` (default 15 s) is layered on top as extra robustness, not the primary buffer. The BLIB's per-replicate `startTime` / `endTime` rows are the measured peak edges chosen by DIA-NN (or whatever picked the peaks); taking `MIN` / `MAX` across replicates gives a window that already covers the observed RT spread. Don't add a second peak-shape padding pass downstream of the firing-window construction.
- **MTM slot edges reserve room for the solo quadrupole window.** Every member peptide's `PrmIsolationWidthTh`-wide quadrupole window (default 0.7 Th, centered on the precursor m/z) must fit fully inside the slot's nominal `IsolationWindowTh`. The scheduler keeps every member center at least `PrmIsolationWidthTh / 2` from each slot edge (0.35 Th with the defaults), so the maximum span of precursor centers per slot is `IsolationWindowTh - PrmIsolationWidthTh`. The Thermo CSV writer emits `memberSpan + PrmIsolationWidthTh` as the instrument isolation width to match.
- **Modification-syntax normalization**: DIA-NN `C(UniMod:4)`, Carafe `_C[UniMod:4]_`, and Skyline `C[UniMod:4]` are all the same chemistry. `NormalizeModifiedSequence` folds them to Skyline's preferred form before any downstream use.
- **Golden fixtures in `testdata/`** are authoritative. Algorithm changes that move the goldens must regenerate them via `tools/regen-golden.py` running the same code path in the notebook prototype, and the diff must be inspected (not just accepted).

## Scheduler Algorithm Notes

The published webinar algorithm is the reactive cover pass: walk a protein's score-sorted peptide queue, take the first one that fits the RT-budget constraint. When the best-score peptide would push the concurrent count over budget at its RT, fall back to the next in the queue (webinar slide 9: "Balanced Load selects Peptide2 instead").

Cadenza ships two equivalent implementations:

- **Reactive** (`RtAwareCoverSelection = false`): exactly the published flow. `TrySchedule` returns failure when the RT bin is at budget; the cover loop tries the next peptide.
- **Look-ahead** (default, `RtAwareCoverSelection = true`): evaluates each peptide's current `slotsPerBin` saturation before calling `TrySchedule` and picks the lowest-load candidate. Score is the tiebreak. Spreads first peptides across the gradient to reduce hot-bin clumping.

Both modes respect the same RT-budget constraint; the difference is whether the alternate-peptide pick is proactive or reactive. On heavy MTM multiplexing the look-ahead default may under-cover (it steers away from RT bins where existing slots are joinable for free) — the default is provisional pending A/B validation on more datasets. Do not describe look-ahead as "more RT-aware" than reactive; both are RT-aware.

`MaxPeptidesPerProtein` and `MinPeptidesPerProtein` only affect the load-up pass and the post-filter; cover-pass behavior is identical regardless of these knobs. If a user reports "changing max doesn't change coverage," that is expected behavior of the published algorithm, not a bug.

## Code Style

Defer to the user's global `~/.claude/CLAUDE.md` for all language-agnostic preferences (no emojis, no em-dashes, past-tense commit messages, etc.) and the C# / .NET conventions inherited from that file. Repo-specific overrides only:

- xUnit test names read as specifications, in the form `Method_Scenario_ExpectedResult` (e.g. `Scheduler_RtAwareWithFullBin_PicksLessLoadedAlternative`).
- Plot rendering code in `MainWindow.xaml.cs` uses `Color.FromHex("#rrggbb")` for fixed-palette colors so the source is greppable. Computed colors use the helpers in the same file.
- The `ApplyPlotStyle(plt)` helper standardizes axis label / tick / title sizes across plots; new plots must call it.
- ScottPlot 5.0.34 has a broken `Rectangle.FillStyle.Color` setter. Use `Add.Polygon` with four corner points instead.

## Versioning and Release Notes

Skyline Cadenza uses `YY.feature.patch` versioning (e.g., `26.1.0` = 2026, first feature release, no patches).

- **Release notes** are maintained in `release-notes/RELEASE_NOTES_v{version}.md`.
- See `release-notes/README.md` for the full format, conventions, and release process.
- During development, append entries to `RELEASE_NOTES_next.md` as features and fixes land on `main`.
- The version lives in two places that must be kept in sync at release time: `<Version>` in `Directory.Build.props` (assembly) and `Version = ` in `src/SkylineCadenza.App/tool-inf/info.properties` (Skyline tool manifest). The release workflow verifies the latter against the tag before publishing.
- When adding significant features or fixes, add a brief entry to the current draft release notes file in the appropriate section.
