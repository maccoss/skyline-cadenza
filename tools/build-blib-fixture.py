#!/usr/bin/env python3
"""Extract a small, anonymized BLIB fixture from a production BLIB.

Used to seed `testdata/sample.blib` for `BlibRetentionTimeReader` tests.
Picks N peptides with a representative mix of replicate coverage, copies
their rows from every related table, and rewrites source filenames as
`replicate_NN.raw` placeholders so committed fixtures carry no lab paths.

Usage:
    python3 tools/build-blib-fixture.py SOURCE.blib OUTPUT.blib
"""
import argparse
import os
import sqlite3
import sys


def main() -> int:
    p = argparse.ArgumentParser()
    p.add_argument("source")
    p.add_argument("output")
    p.add_argument("--peptides", type=int, default=12,
                   help="Number of distinct peptides to keep.")
    p.add_argument("--sources", type=int, default=6,
                   help="Max source files to keep per peptide.")
    args = p.parse_args()

    if os.path.exists(args.output):
        os.remove(args.output)

    src = sqlite3.connect(f"file:{args.source}?mode=ro", uri=True)
    dst = sqlite3.connect(args.output)
    src.row_factory = sqlite3.Row

    # 1. Re-create schema verbatim in the destination so the reader code
    #    runs against the same definitions.
    for (name, sql) in src.execute(
            "SELECT name, sql FROM sqlite_master "
            "WHERE type IN ('table','index') AND sql IS NOT NULL"):
        # sqlite_sequence is auto-created by SQLite when AUTOINCREMENT
        # tables are first written to; can't be CREATE'd manually.
        if name == "sqlite_sequence":
            continue
        dst.execute(sql)

    # 2. Pick a representative mix of peptides: a few short, a few long,
    #    a few high-charge, ordered by id for determinism.
    cur = src.execute(
        "SELECT id FROM RefSpectra "
        "WHERE peptideSeq IS NOT NULL AND length(peptideSeq) BETWEEN 7 AND 25 "
        "ORDER BY id LIMIT ?", (args.peptides * 8,))
    candidate_ids = [r[0] for r in cur]
    # Stride to spread across the library.
    stride = max(1, len(candidate_ids) // args.peptides)
    pep_ids = candidate_ids[::stride][:args.peptides]
    print(f"Picked {len(pep_ids)} peptide ids: {pep_ids}")

    # 3. Pick a small set of source files. Keep low ids for determinism.
    src_ids = [r[0] for r in src.execute(
        "SELECT id FROM SpectrumSourceFiles ORDER BY id LIMIT ?",
        (args.sources,))]
    print(f"Keeping source ids: {src_ids}")

    # 4. Copy SpectrumSourceFiles with sanitized names.
    for i, sid in enumerate(src_ids, start=1):
        row = src.execute(
            "SELECT * FROM SpectrumSourceFiles WHERE id = ?", (sid,)).fetchone()
        dst.execute(
            "INSERT INTO SpectrumSourceFiles "
            "(id, fileName, idFileName, cutoffScore, workflowType) "
            "VALUES (?, ?, ?, ?, ?)",
            (row["id"], f"replicate_{i:02d}.raw",
             f"replicate_{i:02d}.dia.parquet",
             row["cutoffScore"], row["workflowType"]))

    # 5. Copy LibInfo. The fixture follows the non-redundant `.blib`
    #    pattern (one canonical RefSpectra row per peptide, per-replicate
    #    observations in RetentionTimes), so the LSID type segment is
    #    `nr`. The source library's LSID happens to carry the `redundant`
    #    token even though its data pattern is non-redundant; we don't
    #    propagate that.
    li = src.execute("SELECT * FROM LibInfo").fetchone()
    dst.execute(
        "INSERT INTO LibInfo (libLSID, createTime, numSpecs, majorVersion, minorVersion) "
        "VALUES (?, ?, ?, ?, ?)",
        ("urn:lsid:test:spectral_library:bibliospec:nr:cadenza-fixture",
         li["createTime"], len(pep_ids), li["majorVersion"], li["minorVersion"]))

    # 6. Copy ancillary lookup tables (small, copy whole).
    for t in ("IonMobilityTypes", "ScoreTypes"):
        for row in src.execute(f"SELECT * FROM {t}"):
            cols = row.keys()
            dst.execute(
                f"INSERT INTO {t} ({','.join(cols)}) VALUES ({','.join('?' for _ in cols)})",
                tuple(row[c] for c in cols))

    pep_set = ",".join(str(p) for p in pep_ids)
    src_set = ",".join(str(s) for s in src_ids)

    # 7. Per-peptide rows.
    copied_specs = copied_rts = copied_peaks = 0
    for row in src.execute(
            f"SELECT * FROM RefSpectra WHERE id IN ({pep_set})"):
        cols = row.keys()
        dst.execute(
            f"INSERT INTO RefSpectra ({','.join(cols)}) "
            f"VALUES ({','.join('?' for _ in cols)})",
            tuple(row[c] for c in cols))
        copied_specs += 1

    for row in src.execute(
            f"SELECT * FROM RetentionTimes "
            f"WHERE RefSpectraID IN ({pep_set}) "
            f"  AND SpectrumSourceID IN ({src_set})"):
        cols = row.keys()
        dst.execute(
            f"INSERT INTO RetentionTimes ({','.join(cols)}) "
            f"VALUES ({','.join('?' for _ in cols)})",
            tuple(row[c] for c in cols))
        copied_rts += 1

    for row in src.execute(
            f"SELECT * FROM RefSpectraPeaks WHERE RefSpectraID IN ({pep_set})"):
        cols = row.keys()
        dst.execute(
            f"INSERT INTO RefSpectraPeaks ({','.join(cols)}) "
            f"VALUES ({','.join('?' for _ in cols)})",
            tuple(row[c] for c in cols))
        copied_peaks += 1

    for row in src.execute(
            f"SELECT * FROM Modifications WHERE RefSpectraID IN ({pep_set})"):
        cols = row.keys()
        dst.execute(
            f"INSERT INTO Modifications ({','.join(cols)}) "
            f"VALUES ({','.join('?' for _ in cols)})",
            tuple(row[c] for c in cols))

    # 8. Proteins via RefSpectraProteins join. Keep only proteins linked
    # to the kept peptides.
    prot_ids = set()
    for row in src.execute(
            f"SELECT * FROM RefSpectraProteins WHERE RefSpectraId IN ({pep_set})"):
        cols = row.keys()
        dst.execute(
            f"INSERT INTO RefSpectraProteins ({','.join(cols)}) "
            f"VALUES ({','.join('?' for _ in cols)})",
            tuple(row[c] for c in cols))
        prot_ids.add(row["ProteinId"])

    if prot_ids:
        for row in src.execute(
                f"SELECT * FROM Proteins WHERE id IN ({','.join(str(p) for p in prot_ids)})"):
            cols = row.keys()
            dst.execute(
                f"INSERT INTO Proteins ({','.join(cols)}) "
                f"VALUES ({','.join('?' for _ in cols)})",
                tuple(row[c] for c in cols))

    dst.commit()
    dst.execute("VACUUM")
    dst.commit()
    dst.close()
    src.close()

    size = os.path.getsize(args.output)
    print(f"Wrote {args.output} ({size:,} bytes)")
    print(f"  RefSpectra:     {copied_specs}")
    print(f"  RetentionTimes: {copied_rts}")
    print(f"  RefSpectraPeaks: {copied_peaks}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
