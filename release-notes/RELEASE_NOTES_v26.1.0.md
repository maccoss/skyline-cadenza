# Skyline Cadenza v26.1.0 Release Notes

First public release of Skyline Cadenza, a Skyline external tool for designing load-balanced PRM / MTM targeted assays from gas-phase-fractionated DIA-NN libraries or Carafe AI-predicted spectral libraries. Cadenza ports the prototype notebook at [`maccoss/targeted-modeling`](https://github.com/maccoss/targeted-modeling) (`gpf_coverage.ipynb`) to a self-contained WPF application that runs as a Skyline external tool and writes results back to the active document.

The acquisition-design algorithm follows the published Stellar MS Webinar ("Balancing the Load", 2024) and US 11,688,595 B2 (Remes & MacCoss, 2023): co-eluting precursors that fit in a 2-3 Th isolation window share one acquisition slot when their top-4 predicted fragments do not overlap.

## Distribution

- Self-contained zip (`SkylineCadenza.zip`) built via CI, installable through Skyline-daily's *Tools > External Tools > Add > File...* dialog. Requires Skyline-daily 26.1.1.083 or later and the .NET 8 Desktop Runtime on Windows.
- Apache 2.0 license. Source at <https://github.com/maccoss/skyline-cadenza>.

## Ingest

- **DIA-NN report.parquet ingest**: streams `report.parquet` files via `Parquet.Net` with column projection. Reads DIA-NN's measured fragment columns (`Fr.0.Id`..`Fr.11.Id`) when present, falling back to library predictions otherwise. Tested on the SEA-AD MTG GPF dataset (87k precursors, 7,545 protein groups).
- **Carafe library TSV ingest**: hand-rolled streaming reader for the 2.6 GB Carafe AI-predicted library (~22.8M rows) running end-to-end in ~13 s on NVMe. Filters to detected precursors via a target protein list, keeps top-4 fragments per precursor by `RelativeIntensity`, caches the filtered output to a SHA-1-keyed parquet so reruns skip the streaming step.
- **Load library from running Skyline document (preview)**: custom report-definition probe over the JSON-RPC interface. Tries six peptide-level RT column names in order (`PredictedRetentionTime`, `LibraryRetentionTime`, `AverageMeasuredRetentionTime`, `BestRetentionTime`, `PeptideRetentionTime`, `RetentionTime`) and validates that the probe sample contains at least one non-zero value before accepting. Falls back to a user-supplied **RT column override** text field for documents whose RT column isn't in the default list.
- **DIA-NN q-value filter** at ingest (default 0.01).
- Modification-syntax normalization: DIA-NN's `C(UniMod:4)` and Carafe's `_C[UniMod:4]_` are both folded to Skyline's preferred `C[UniMod:4]` before any downstream use.

## Scheduling

- **Two-pass greedy scheduler** matching the published webinar algorithm: cover one peptide per protein group, then add additional peptides per protein up to `MaxPeptidesPerProtein` until no more fit.
- **Strict intersection-based co-elution** for MTM slot sharing: two precursors only multiplex if their unpadded peak RT ranges overlap (not merely their padded firing windows).
- **Top-4 fragment non-clash check** using two-pointer merge over sorted fragment-mz arrays.
- **Configurable isolation-window cap** (default 3.0 Th), **fragment tolerance** (default 0.5 Da), and **PRM / solo-slot width** (default 0.7 Th).
- **Cycle budget enforcement** as an RT-bin-local concurrent slot cap (default 100); the budget check fires per-bin so a single peptide consumes one slot only at its specific RT.
- **RT buffer (each side)** (default 15 s) padded onto every peak's `RT.Start`/`RT.Stop` before scheduling. Slot firing window = union of padded member ranges.
- **Charge handling per slot**: same-charge-per-slot (default) or allow-mixed with majority-charge reporting in the Thermo CSV output.
- **Look-ahead load balancing in cover pass** (default on, toggleable): the cover pass evaluates each protein's peptides against the current `slotsPerBin` saturation and picks the lowest-load candidate before calling `TrySchedule`, instead of waiting for the highest-scoring peptide to fail and falling back. Both modes implement the published RT-budget rule and slide-9 fallback behavior; they differ only in whether the alternate-peptide pick happens proactively or reactively.
- **Min / max peptides per protein**: post-filter that drops protein groups failing the minimum and frees their slot capacity from the occupancy curve.

## Parsimony

- Streamlined port of the lab's `skyline_prism.parsimony` algorithm: inverted-index protein subsumption plus a single-pass razor assignment.
- ~70x faster than the reference notebook implementation on the test dataset, byte-for-byte equivalent on the committed golden fixtures.
- Categorizes each peptide as `unique`, `razor`, or `shared`; the scheduler ranks unique-before-razor within each protein's queue.

## Target Protein List

- Paste FASTA, newline-separated accessions, or gene symbols, or open a `.fasta` / list file from disk.
- Gene-symbol-to-accession resolution against the loaded library.
- **Target list first, fill with rest** (default): scheduler covers target proteins first, then fills remaining budget with off-list proteins.
- **Exclusive (target list only)**: off-list proteins are ignored entirely.

## UI and Plots

- Live-update WPF UI: every parameter bound to a slider, radio button, or checkbox. Each change reruns the scheduler in the background (debounced via `CancellationToken`) and refreshes plots in under a second on the SEA-AD library.
- **Summary strip** above the plots tracks library counts vs. scheduled counts in real time: precursors, peptides, protein groups, MTM slots, slots multiplexed, peak load, median peptides per protein.
- Five plot tabs powered by ScottPlot 5 WPF:
  - **Cycle load**: concurrent MTM acquisition slots vs. retention time with budget reference line.
  - **Protein coverage**: per-protein dot of `log10(summed precursor intensity)`, sorted high → low, colored by peptides scheduled per protein (grey = 0, red = 1, ..., dark green = 5+).
  - **Per-run coverage**: per-DIA-NN-run curves; auto-detects the `Chrlib400-500` GPF naming pattern and stacks per-GPF curves with a dashed black sum.
  - **m/z x RT heatmap**: library view shows binned precursor density; scheduled view draws every slot as a polygon (width = max(member m/z spread, PRM cap), height = padded firing window), colored by multiplex depth.
  - **Slot occupancy**: full histogram of precursors-per-slot (1 through the observed maximum) with max / median / mode in the title. Levels 1-5 use distinct hues; levels 6+ use a darkening-purple ramp.

## Outputs

- **Push targets to Skyline**: writes a peptide-style transition list CSV to a temp file and runs `SkylineCmd --import-transition-list=<path>` against the live document. Imported entries land in the protein/peptide tree. Runs asynchronously on a background thread so the Cadenza UI stays responsive; the status line quotes an ETA up front (based on observed ~10 transition rows / sec throughput) and updates every 10 s with elapsed time and estimated percent complete. Live per-protein progress remains in Skyline's Immediate Window.
- **Export Thermo CSV**: scheduled inclusion list compatible with Thermo Method Editor's *Import Scheduled List*. For MTM mode, emits one row per slot with member peptide names joined by ` | `, slot center m/z, majority charge, co-elution midpoint, full padded firing window, and `max(member spread, PRM width)` as the isolation window. For PRM mode, one row per precursor.

## Skyline Integration

- Auto-connects on launch via `args[0]` (the named-pipe name Skyline passes to external tools), wrapping the prefix as `SkylineMcpJson-` for the JSON-RPC interface.
- Connect-per-call pattern: each RPC call opens a fresh pipe, executes the call, and disposes. Avoids pipe-held-open issues that previously surfaced as `JsonReaderException` after long idle periods.
- `GetDocumentStatus` polled every 2 s; status line tracks the active document name and live precursor / peptide / protein counts.
- Vendored copies of the four pwiz `SkylineTool` client sources (`SkylineJsonToolClient`, `IJsonToolService`, `JsonToolConstants`, `JsonToolModels`) link-compiled into `SkylineCadenza.Core`. Apache 2.0.

## Testing and Packaging

- 21 xUnit tests covering parsimony, scheduler, fragment-clash, Carafe TSV reader, and golden-file regressions against the notebook prototype.
- MSBuild packaging target (`build/package.proj`) publishes the WPF app framework-dependent against the .NET 8 Desktop Runtime and zips the output plus `tool-inf` into `SkylineCadenza.zip` ready for Skyline's external-tools installer.
- Continuous integration via GitHub Actions: `windows-latest` runner, dotnet 8, builds and tests on every push to `main` / `develop` and on every pull request. The `release.yml` workflow triggers on `v*` tags, verifies that curated release notes and the `info.properties` version both match the tag, then attaches `SkylineCadenza-v{version}.zip` to a GitHub Release with the release notes as the body.

## Known Limitations

- **RT boundaries from Skyline are synthesized**: the loader currently uses a peptide-level RT column ± 0.30 min as the firing window. Skyline knows actual per-replicate `MaxStartTime` / `MinEndTime` for documents with imported chromatograms, but Cadenza doesn't query them yet. Tracked for a follow-up release.
- **Skyline transition-list import is slow**: the `--import-transition-list` path runs at ~10 transition rows / sec because Skyline's `MassListImporter` rebuilds the document tree per protein. A 30k-row schedule blocks for 30-60 minutes. The async push + heartbeat keeps Cadenza usable during the wait, but a faster RPC method (e.g. `InsertProteomicTransitionList` that bypasses `MassListImporter`) would need to be added on the Skyline side.
- **Look-ahead load balancing default is provisional**: on MTM-mode data with heavy multiplexing, look-ahead can under-cover relative to reactive (proactive low-load picks steer away from RT bins where existing slots are joinable for free). Pending A/B validation on additional datasets the default may flip to off; the option is exposed in the UI either way.
- **Per-GPF heatmap panels**: the m/z x RT heatmap currently shows a single combined view. Per-GPF stacked panels are pending.

## References

- Heil L. et al., *Closing the gap between targeted and untargeted measurements using intelligent data acquisition on Stellar MS*, ASMS 2025.
- Remes P.R. & MacCoss M.J., US 11,688,595 B2, *Mass spectrometry methods*, 2023.
- Stellar MS Webinar 2024, *Balancing the Load: maximizing instrument time and performance characteristic to increase targeted data quality*.
- ProteoWizard pwiz: <https://github.com/ProteoWizard/pwiz>.
- Prototype notebook: <https://github.com/maccoss/targeted-modeling> (`gpf_coverage.ipynb`).
