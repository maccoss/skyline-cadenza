# Cadenza analysis notebooks

`algorithm-comparison.ipynb` walks through how Cadenza's three coverage objectives
(`Balanced`, `Maximize Protein Coverage`, `Maximize Peptide Coverage`) actually
differ on a synthetic dataset. The notebook is structured as:

1. **Toy walkthrough** — a Python re-implementation of the scheduler simplified to
   a 5-protein scenario where every algorithm's decision can be inspected by
   hand. Useful for teaching the *why* but not the production code path.
2. **Realistic synthetic example** — a ~150-protein assay generated in Python,
   serialised to JSON, and scheduled by the actual C# scheduler via the
   `SkylineCadenza.Cli` companion executable. The figures reflect the real
   algorithm bit-for-bit.

## Files

- `algorithm-comparison.ipynb` — the notebook. Committed pre-rendered so the
  outputs are visible on GitHub without re-running anything.
- `_build_notebook.py` — generator that produces the `.ipynb` from a flat list
  of cells. Edit this rather than the `.ipynb` directly; the `.ipynb` is the
  build output.
- `figures/` — PNG plots saved by the notebook. Embedded in the README via
  relative paths.

## Re-render after Cadenza scheduler changes

```bash
# From the repo root.
dotnet build src/SkylineCadenza.Cli -c Release   # one-time; the notebook expects this
python notebooks/_build_notebook.py              # rebuild the .ipynb from cells
jupyter nbconvert --to notebook --execute \
    notebooks/algorithm-comparison.ipynb --inplace
```

The notebook calls `dotnet run --project src/SkylineCadenza.Cli --no-build` per
coverage objective during Section 2, so the figures show the actual C#
scheduler's output for the seeded synthetic assay.

## Editing cell content

Open `_build_notebook.py` and edit the `CELLS` list — each entry is a
`(cell_type, source_string)` tuple. Markdown cells use standard markdown; code
cells get an empty `outputs` array stamped on so `nbconvert --execute` populates
them on the next render.

The notebook intentionally has no checked-in cell IDs (`nbformat` warns but
doesn't error). If you care about that, run `jupyter nbformat normalize`.
