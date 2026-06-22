# Skyline Cadenza v26.2.2 Release Notes

Patch release fixing the Thermo inclusion CSV column headers so Method Editor maps them to the right columns instead of falling back to a full-gradient inclusion window.

## Bug Fixes

- **Thermo CSV column headers** now match the Thermo Method Editor
  Mass List Table schema exactly. Previously the writer emitted
  `t (min)`, `Window (min)`, and `Normalized CE`; Method Editor could
  not map those to its columns and silently defaulted every entry to
  a full-gradient (0 to end-of-run) inclusion window, defeating
  scheduling. The writer now emits `t start (min)`, `t stop (min)`,
  and `HCD Collision Energy`. The two RT columns carry the padded
  scheduling window boundaries (the same interval the scheduler
  costed against the cycle budget) instead of a midpoint + width.
  The Adduct column is also now blank, matching what Method Editor
  expects for peptide entries (the z column carries the
  protonation).
