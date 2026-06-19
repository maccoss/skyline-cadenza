# Coverage objectives

Cadenza schedules a PRM or MTM assay with a greedy two-pass algorithm.
The user picks one of three objectives (Balanced, Maximize Proteins,
Maximize Peptides) and the choice changes two things: which peptide the
cover pass picks first for each protein, and how many peptides the
load-up pass adds per protein. Everything else (slot-edge isolation rule,
RT budget, co-elution intersection, fragment-clash check, charge
handling) is identical across all three objectives.

This document covers what each objective does, why, and when to use it.
For knob-level parameter descriptions (cycle budget, fragment tolerance,
firing pad, etc.) see [`settings.md`](settings.md). For the per-mode
parameter recommendation Cadenza pushes into Skyline see
[`skyline-integration.md`](skyline-integration.md).

## The fixed two-pass shape

For every objective the scheduler does:

1. **Cover pass**: walk every protein group once. For each group, pick at
   most one peptide and try to schedule it.
2. **Load-up pass**: round-robin over the protein groups that got a
   peptide in pass 1. For each, try to add one more peptide. Repeat
   until a full round adds nothing.

A peptide "fits" if it can either join an existing MTM slot or open a
new slot inside the per-RT-bin cycle budget. To join an existing slot:

- The slot's nominal isolation window must still contain every member's
  quadrupole window (the slot-edge rule: every member's center sits at
  least `PrmIsolationWidthTh / 2` from each slot edge, so the max
  center-to-center span in one slot is
  `IsolationWindowTh - PrmIsolationWidthTh`).
- The new peptide's unpadded RT range must intersect the slot's
  co-elution range non-emptily (strict co-elution: bounding-box overlap
  alone isn't enough, the actual peaks must overlap so the precursors
  are sampled at the same instant).
- The new peptide's top-4 fragment m/z values must not clash with any
  existing slot member's fragments within `FragmentTolDa`.
- Charge: with `SameChargePerSlot` the candidate's charge must equal the
  slot's first member's charge.
- Extending the slot's padded firing window must not push any new RT bin
  over `CycleBudget`.

To open a new slot: the candidate's padded firing window's max-loaded RT
bin must have at least one slot of headroom under `CycleBudget`.

The cycle budget is per RT bin, not global. A protein with peptides at
RT 10, 30, 50, 70 consumes one slot per peptide in their respective bins
only; this does NOT block 4 budget elsewhere.

## Balanced (exact published webinar algorithm)

The reproducible baseline. Matches the Stellar MS Webinar 2024 "Balancing the Load" algorithm slide for slide.

**Cover pass**: reactive. Walk the protein's score-sorted candidate
queue (peptide ranking knob). Try the highest-scoring peptide first;
if its padded firing window pushes any RT bin over budget, fall back
to the next peptide in score order. Webinar slide 9 ("Balanced Load
selects Peptide2 instead") is exactly this behaviour.

**Load-up pass**: round-robin to `MaxPeptidesPerProtein` (the
user-facing "Max peptides per protein" slider).

**When to pick this**: when you want a reproducible baseline that
matches the published algorithm, or when you're A/B-testing Cadenza
against the original notebook prototype. Otherwise prefer
Maximize Proteins.

## Maximize Proteins (default)

Default objective. Optimises for the number of protein groups with at
least one peptide scheduled.

**Cover pass**: look-ahead with prefer-joinable.

1. Walk the protein's score-sorted queue once and accept the first
   peptide that can join an existing slot without opening a new one.
   Joining consumes zero cycle budget (the slot's RT bins are already
   counted) and zero new slot, so it's free coverage.
2. If no peptide is joinable, evaluate every queue position's RT-bin
   saturation (`max(slotsPerBin)` over its padded firing window) and
   pick the least-saturated. Score is the tiebreaker.
3. If even the least-saturated pick doesn't fit (fragment clash,
   co-elution, charge), fall through to a static-order walk for any
   peptide that schedules at all.

**Load-up pass**: capped at `MinPeptidesPerProtein` (the user-facing
"Min peptides per protein" knob). The saved cycle budget stays
available for first-peptide coverage of other proteins.

With the default `MinPeptidesPerProtein = 1`, the load-up pass is
effectively a no-op under this objective: cover one peptide per
covered protein, stop. With `MinPeptidesPerProtein = 2` it tops up
every covered protein to two peptides and then stops.

**When to pick this**: most assays. You want the broadest possible
protein coverage and you don't mind that some proteins get only one
peptide.

## Maximize Peptides

Maximises the total number of scheduled peptides without a per-group
cap.

**Cover pass**: look-ahead, but WITHOUT the prefer-joinable preference.
Pick the queue position with the lowest current `max(slotsPerBin)`
saturation under its padded firing window; score is the tiebreaker.
Same fallback to static-order if the RT-aware pick doesn't fit.

(Why drop prefer-joinable? In Maximize Peptides we don't need to save
budget for first-peptide coverage of more proteins; the load-up pass
is uncapped, so anywhere we don't spend budget in the cover pass gets
spent in load-up anyway. Prefer-joinable would steer all the cover-pass
peptides into a small number of dense slots, leaving the load-up pass
to make a similarly dense second pass.)

**Load-up pass**: uncapped. `MaxPeptidesPerProtein` is ignored. The
round-robin loop continues until either the cycle budget saturates
everywhere or every protein has exhausted its candidate queue. The
round-robin order is preserved, so a protein never gets its (k+1)th
peptide before every other covered protein has had a chance at its kth.

**When to pick this**: when you specifically want as many quantifiable
peptides as possible for downstream pathway-level or stoichiometry
analyses, and you're willing to spend more cycle budget on the
top-quantity proteins to do it.

## What the load-up cap looks like by objective

| Objective         | Effective `MaxPeptidesPerProtein` cap |
| ----------------- | ------------------------------------- |
| Balanced          | the slider value (default 5)          |
| Maximize Proteins | clamped to `MinPeptidesPerProtein` (default 1) |
| Maximize Peptides | `int.MaxValue` (the slider is ignored) |

This is also why a user report of "I'm in Maximize Proteins and
changing Max peptides per protein doesn't change anything" is expected
behaviour rather than a bug: in Maximize Proteins the load-up cap is
`Min`, not `Max`. Similarly in Balanced, the cover pass result is
unaffected by `Max` (the cap only applies during load-up); a report of
"Balanced with my Max change doesn't move the coverage" is also
expected, since coverage is set during the cover pass.

## Why the cover pass goes smallest groups first

Independent of objective, the cover pass orders protein groups by:

1. Target-list membership (when the user supplied a target list with
   `FirstThenFill`, on-list groups go first).
2. Protein priority (Summed Intensity, Protein Q-Value, or Provided
   List Order).
3. Smallest candidate-queue size first.
4. Alphabetical (for stable ordering).

Smallest-queue-first matters because small queues have the fewest
fallbacks. If a protein has one candidate and that candidate doesn't
fit, the protein is dropped. Trying small queues first while the cycle
budget is mostly empty maximises their chance of placing.

## Validation

Two notebooks demonstrate the objective behaviours on a real DIA-NN
report:

- `notebooks/algorithm-comparison.ipynb` runs all three objectives
  on the same candidate pool and renders side-by-side plots of
  proteins covered, peptides scheduled, peak load curve, and slot
  composition.
- `tools/regen-golden.py` regenerates the golden fixtures in
  `testdata/` from the targeted-modeling notebook prototype; the
  Balanced objective matches these byte for byte.

Both notebooks consume the Cadenza CLI (`SkylineCadenza.Cli`), which
runs the same scheduler the WPF app uses.
