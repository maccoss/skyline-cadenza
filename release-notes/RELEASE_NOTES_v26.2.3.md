# Skyline Cadenza v26.2.3 Release Notes

Patch release fixing the target-list "Exclusive" mode so a bare-accession target list matches the full Skyline / UniProt-style protein names carried by candidates loaded from a Skyline document.

## Bug Fixes

- **Target list "Exclusive" mode now matches when the target list is bare
  accessions and the candidate pool carries full Skyline / UniProt-style
  protein names** (e.g. target list says `P55011`, Skyline document's
  protein is `sp|P55011|S12A2_HUMAN`). Previously the filter did an
  exact string compare and silently dropped every group whose name
  carried the pipe-prefix; symptoms were "I have 416 proteins mapped to
  my target FASTA but Cadenza schedules 2". The filter now also tries
  the bare accession extracted from `sp|...|...` / `tr|...|...` style
  identifiers, so a bare-accession target list matches the full Skyline
  protein name. DIA-NN and Carafe ingest paths already produced bare
  accessions and are unaffected. Edge case: if the user targets a
  protein whose group was canonicalised by parsimony to a different
  indistinguishable member, that target still won't match; pass the
  canonical member instead (this is rare and pre-existing).
