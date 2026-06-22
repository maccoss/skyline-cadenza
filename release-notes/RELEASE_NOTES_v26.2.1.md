# Skyline Cadenza v26.2.1 Release Notes

Patch release fixing the Maximize Proteins load-up cap so leftover cycle budget is used after coverage converges.

## Bug Fixes

- Maximize Proteins now honours **Max peptides per protein** during
  load-up. Previously the load-up loop was capped at Min, so after
  every protein had its first peptide the leftover cycle budget went
  unused. The cover pass is single-pass; there is no second cover
  attempt to save budget for, so the load-up loop now fills toward
  Max (default 5) once coverage converges. The fill is breadth-first
  round-robin across proteins, lap by lap (lap 1 takes every protein
  up to 2 peptides where possible, lap 2 up to 3, etc.), so no
  single protein is loaded to Max before others get their 2nd.
  Proteins with only 1 or 2 viable peptides stop early and don't
  block the others.
