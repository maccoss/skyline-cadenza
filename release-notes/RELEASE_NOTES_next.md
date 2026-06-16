# Skyline Cadenza Release Notes (Next Release)

Working draft for the next release. Append entries here as features and fixes land on `main`. At release time this file is renamed to `RELEASE_NOTES_v{version}.md` and `Version` in `src/SkylineCadenza.App/tool-inf/info.properties` is updated to match.

## New Features

- **BLIB-sourced firing windows for "Load from Skyline document".** Cadenza now reads per-peptide measured peak boundaries directly from the BiblioSpec `.blib` files the active Skyline document references, via `GetSettingsListSelectedItems("Libraries")` + `GetSettingsListItem` and direct SQLite read of the BLIB's `RetentionTimes` table. The firing window is the union (`MIN(startTime)`, `MAX(endTime)`) across every replicate that detected the peptide, filtered to observations whose identification confidence satisfies the same `QValueCutoff` used at ingest. When a document references multiple BLIBs (e.g. a measured DIA-NN library plus a predicted Carafe / Prosit library), each library is scored by per-replicate boundary variance and the higher-scoring library wins per peptide — so DIA-NN naturally outranks predicted libraries without Cadenza having to inspect their LSIDs. Falls back to `RT ± peakHalfWidthMin` synthesis for peptides not covered by any BLIB. Status line on a successful load now reports "N of M firing windows from BLIB".
- **Coverage objective selector** in the scheduling panel. *Balanced* (default) preserves the published webinar behavior. *Maximize protein coverage* makes the cover pass prefer peptides that join an existing slot for free over peptides that would open a new slot, conserving budget for first-peptide coverage of additional proteins, and caps load-up at `MinPeptidesPerProtein`. *Maximize peptide coverage* uncaps the load-up pass past `MaxPeptidesPerProtein` while preserving round-robin fairness across proteins.

## Bug Fixes

- **Protein-coverage plot title now reports covered-of-total.** Previously the title showed only the total group count (`n = 7,740 groups`), which obscured the fact that the "Not scheduled" greys are visually overdrawn by adjacent colored markers at high density. The title now reads `(6,634 of 7,740 groups covered)` so the visible curve and the title agree on what is actually distinguishable.
- **Protein-coverage "5+ peptides" legend entry now reads "5 peptides" when no protein exceeds 5.** Under the default Balanced objective with `MaxPeptidesPerProtein = 5` nothing can reach 6, so the previous "5+" wording was misleading. The legend is dynamic and reverts to "5+ peptides" under the new MaximizePeptides objective when the load-up actually exceeds 5.
- **"Load from Skyline document" button no longer carries the (preview) tag.** Precursor / fragment ingest from a running Skyline document is now backed end-to-end: report probe for the spectral data, BLIB read for the per-replicate firing windows (see the New Features entry above).
- **MTM slot edges now reserve room for the solo quadrupole window.** Every member peptide's quadrupole window (`PrmIsolationWidthTh` wide, default 0.7 Th, centered on the precursor m/z) must fit fully inside the slot's nominal isolation window. Cadenza now requires every peptide center to sit at least `PrmIsolationWidthTh / 2` from each slot edge. Previously, peptide centers were allowed to span the full `IsolationWindowTh`, leaving precursors at the extreme of a slot with half their quadrupole window outside the nominal slot edge. For the default 3.0 / 0.7 pair this caps the span of precursor centers at 2.3 Th; for any other window / solo pair the rule scales naturally. The Thermo CSV writer now reports `memberSpan + PrmIsolationWidthTh` as the instrument isolation width so the actual instrument firing covers what the scheduler reserved.

## Performance

<!-- none yet -->

## Breaking Changes

<!-- none yet -->
