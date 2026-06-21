"""Regenerate the figures embedded in docs/coverage-objectives.md.

Run from the repo root:

    .venv/bin/python docs/figures/generate.py

Outputs PNG files into the same directory as this script.

Layout discipline: every figure pads its title above the axes, keeps
legends inside the axes (or in a dedicated row), and uses
constrained_layout so titles and tick labels never overlap. Verify
visually after any change to a figure.
"""

from __future__ import annotations

from pathlib import Path

import matplotlib.patches as mpatches
import matplotlib.pyplot as plt
import numpy as np

OUT_DIR = Path(__file__).resolve().parent

plt.rcParams.update(
    {
        "figure.dpi": 130,
        "savefig.dpi": 150,
        "font.family": "DejaVu Sans",
        "font.size": 11,
        "axes.spines.top": False,
        "axes.spines.right": False,
        "axes.labelsize": 11,
        "axes.titlesize": 12,
        "axes.titlepad": 14,
        "legend.fontsize": 9.5,
        "legend.frameon": False,
    }
)

PALETTE = {
    "covered": "#3b82f6",
    "loadup": "#f97316",
    "skipped": "#cbd5e1",
    "budget": "#ef4444",
    "load_curve": "#1e293b",
    "slot_a": "#22c55e",
    "slot_b": "#a855f7",
    "pad_face": "#bfdbfe",
    "peak_face": "#1d4ed8",
}


def _save(fig: plt.Figure, name: str) -> None:
    out = OUT_DIR / f"{name}.png"
    fig.savefig(out, bbox_inches="tight", facecolor="white")
    plt.close(fig)
    print(f"wrote {out.relative_to(OUT_DIR.parent.parent)}")


# Figure 1: the two-pass shape.
def figure_two_pass() -> None:
    fig, ax = plt.subplots(figsize=(8.0, 3.8), constrained_layout=True)

    proteins = ["Protein A", "Protein B", "Protein C", "Protein D", "Protein E"]
    n_peps = 4
    pass1_pick = [0, 1, 0, 0, 2]
    pass2_extras = {0: [1], 1: [2], 2: [1, 2], 3: [1]}

    for i, name in enumerate(proteins):
        for j in range(n_peps):
            if pass1_pick[i] == j:
                colour = PALETTE["covered"]
            elif j in pass2_extras.get(i, []):
                colour = PALETTE["loadup"]
            else:
                colour = PALETTE["skipped"]
            ax.scatter(
                j, i, s=400, color=colour, edgecolor="white", linewidths=1.2, zorder=3
            )
            ax.text(j, i, f"#{j+1}", ha="center", va="center",
                    fontsize=9, color="white", zorder=4)
        ax.text(-0.6, i, name, ha="right", va="center", fontsize=11)

    ax.set_xlim(-1.5, n_peps - 0.3)
    ax.set_ylim(-0.7, len(proteins) - 0.3)
    ax.set_yticks([])
    ax.set_xticks(range(n_peps))
    ax.set_xticklabels(["best", "2nd", "3rd", "4th"], color="#475569")
    ax.set_xlabel("each protein's candidate peptides, ranked", color="#475569")
    ax.set_title(
        "Two passes: cover first, then top up",
        loc="left", fontweight="bold",
    )

    handles = [
        mpatches.Patch(color=PALETTE["covered"], label="picked in pass 1 (cover)"),
        mpatches.Patch(color=PALETTE["loadup"], label="picked in pass 2 (top up)"),
        mpatches.Patch(color=PALETTE["skipped"], label="not picked"),
    ]
    ax.legend(handles=handles, loc="lower right", bbox_to_anchor=(1.0, -0.32),
              ncol=3, fontsize=10)
    _save(fig, "two-pass-shape")


# Figure 2: per-RT-bin cycle budget.
# Matches the scheduler rule: each peptide consumes one budget slot at
# every RT bin its padded scheduling window (peak boundary + drift
# buffer on each side) touches.
def figure_cycle_budget() -> None:
    fig, (ax_pep, ax_load) = plt.subplots(
        2, 1, figsize=(8.6, 5.2), sharex=True, constrained_layout=True,
        gridspec_kw={"height_ratios": [2.5, 1.5]},
    )

    rng = np.random.default_rng(42)
    n_peaks = 22
    apexes = rng.uniform(4.0, 58.0, size=n_peaks)
    peak_halfwidths = rng.uniform(0.20, 0.60, size=n_peaks)
    pad = 0.25
    rows = rng.integers(0, 6, size=n_peaks)

    for apex, hw, row in zip(apexes, peak_halfwidths, rows):
        ax_pep.barh(
            row, 2 * (hw + pad), left=apex - hw - pad,
            height=0.55, color=PALETTE["pad_face"], alpha=0.9,
            edgecolor="#1d4ed8", linewidth=0.5,
        )
        ax_pep.barh(
            row, 2 * hw, left=apex - hw,
            height=0.55, color=PALETTE["peak_face"], alpha=0.95,
        )

    ax_pep.set_yticks([])
    ax_pep.set_ylim(-0.7, 6.2)
    ax_pep.set_xlim(2, 60)
    ax_pep.set_title(
        "Each peptide is watched across its scheduling window",
        loc="left", fontweight="bold",
    )

    pep_legend = [
        mpatches.Patch(color=PALETTE["peak_face"], label="measured peak"),
        mpatches.Patch(color=PALETTE["pad_face"], label="drift buffer (time padding)"),
    ]
    ax_pep.legend(handles=pep_legend, loc="upper right", ncol=1)

    rt = np.linspace(2, 60, 4000)
    concurrent = np.zeros_like(rt)
    for apex, hw in zip(apexes, peak_halfwidths):
        lo, hi = apex - hw - pad, apex + hw + pad
        concurrent += ((rt >= lo) & (rt <= hi)).astype(float)

    # Pick the budget so that the peak-load region is genuinely over,
    # otherwise the figure doesn't illustrate its own rule. We aim for
    # budget = max_load - 1.
    peak_load = int(concurrent.max())
    budget = max(1, peak_load - 1)
    over = concurrent > budget

    ax_load.fill_between(rt, 0, concurrent, color="#dbeafe", alpha=0.85)
    ax_load.plot(rt, concurrent, color=PALETTE["load_curve"], linewidth=1.6,
                 label="peptides being watched")
    ax_load.axhline(budget, color=PALETTE["budget"], linewidth=1.5,
                    linestyle="--", label=f"cycle budget = {budget}")
    if over.any():
        ax_load.fill_between(rt, budget, concurrent, where=over,
                             color="#fecaca", alpha=0.85,
                             label="over budget: no new peptide fits here")

    ax_load.set_ylim(0, max(peak_load + 2.5, budget + 3))
    ax_load.set_xlabel("retention time (min)")
    ax_load.set_ylabel("count")
    # Legend below the panel so it can't overlap the curve or annotation.
    ax_load.legend(loc="upper center", bbox_to_anchor=(0.5, -0.25),
                   ncol=3, fontsize=9.5)

    # Annotate the peak-load location explicitly.
    peak_idx = int(np.argmax(concurrent))
    sample_rt = float(rt[peak_idx])
    for ax in (ax_pep, ax_load):
        ax.axvline(sample_rt, color="#94a3b8", linewidth=0.8, linestyle=":")
    ann_x = sample_rt + 10 if sample_rt < 30 else sample_rt - 10
    ax_load.annotate(
        f"peak load = {peak_load} peptides\nat RT {sample_rt:.1f} min",
        xy=(sample_rt, peak_load), xytext=(ann_x, peak_load + 1.4),
        fontsize=9.5, color="#1e293b", ha="center",
        arrowprops=dict(arrowstyle="->", color="#475569", lw=0.8),
        bbox=dict(boxstyle="round,pad=0.3", fc="white", ec="#cbd5e1", lw=0.6),
    )

    _save(fig, "cycle-budget-per-rt")


# Figure 3: the three objectives side by side on the same pool.
def figure_three_objectives() -> None:
    proteins = ["A", "B", "C", "D", "E"]
    n_peps = 4

    balanced = {(i, 0) for i in range(5)}
    balanced |= {(i, 1) for i in range(5)}
    balanced |= {(0, 2), (2, 2)}

    max_proteins = {(i, 0) for i in range(5)}

    max_peptides = {(i, j) for i in range(5) for j in range(n_peps)}

    objectives = [
        ("Balanced", balanced, "matches the published webinar"),
        ("Maximize Proteins", max_proteins, "default: as many proteins as possible"),
        ("Maximize Peptides", max_peptides, "as many peptides as instrument allows"),
    ]

    fig, axes = plt.subplots(1, 3, figsize=(12.0, 4.0),
                             sharey=True, constrained_layout=True)

    for ax, (name, picks, sub) in zip(axes, objectives):
        for i in range(len(proteins)):
            for j in range(n_peps):
                colour = PALETTE["covered"] if (i, j) in picks else PALETTE["skipped"]
                ax.scatter(j, i, s=260, color=colour, edgecolor="white",
                           linewidths=1.0, zorder=3)
        ax.set_xticks(range(n_peps))
        ax.set_xticklabels(["best", "2nd", "3rd", "4th"], fontsize=9, color="#475569")
        ax.set_yticks(range(len(proteins)))
        ax.set_yticklabels([f"Protein {p}" for p in proteins], fontsize=10)
        ax.set_xlim(-0.7, n_peps - 0.3)
        ax.set_ylim(-0.7, len(proteins) - 0.3)
        ax.set_title(f"{name}\n{sub}", loc="left", fontsize=11)

    handles = [
        mpatches.Patch(color=PALETTE["covered"], label="scheduled"),
        mpatches.Patch(color=PALETTE["skipped"], label="not scheduled"),
    ]
    fig.legend(handles=handles, loc="outside lower center", ncol=2)
    fig.suptitle(
        "Same 5 proteins, three objectives, three different peptide selections",
        fontweight="bold", x=0.0, ha="left",
    )
    _save(fig, "three-objectives")


# Figure 4: joining vs opening a slot for MTM mode.
def figure_join_vs_open() -> None:
    fig, axes = plt.subplots(1, 2, figsize=(11.0, 4.3),
                             constrained_layout=True)

    iso = 3.0
    pad_rt = 0.6
    rt_lo, rt_hi = 5.0, 12.0

    for ax in axes:
        ax.set_xlim(rt_lo, rt_hi)
        ax.set_ylim(393, 414)
        ax.set_xlabel("retention time (min)")
        ax.set_ylabel("precursor m/z (Th)")

    # Left: joining an existing slot.
    ax_join = axes[0]
    # Spread the two peptides further apart in m/z so the labels can
    # sit one above and one below without colliding.
    apex1, mz1, w1 = 7.8, 400.5, 0.4
    apex2, mz2, w2 = 8.4, 403.0, 0.45

    slot_mz_min = min(mz1, mz2) - iso / 2
    slot_mz_max = max(mz1, mz2) + iso / 2
    slot_rt_min = min(apex1 - w1, apex2 - w2) - pad_rt
    slot_rt_max = max(apex1 + w1, apex2 + w2) + pad_rt

    ax_join.add_patch(mpatches.Rectangle(
        (slot_rt_min, slot_mz_min),
        slot_rt_max - slot_rt_min,
        slot_mz_max - slot_mz_min,
        facecolor=PALETTE["slot_a"], alpha=0.25,
        edgecolor=PALETTE["slot_a"], linewidth=1.5,
    ))
    # Label Peptide 1 BELOW its marker, Peptide 2 ABOVE its marker, so
    # they don't fight for the same vertical real estate.
    for apex, mz, w, label, dy in [
        (apex1, mz1, w1, "Peptide 1", -14),
        (apex2, mz2, w2, "Peptide 2", 12),
    ]:
        ax_join.plot([apex - w, apex + w], [mz, mz], color="#1e293b", linewidth=2)
        ax_join.scatter(apex, mz, color="#1e293b", s=42, zorder=3)
        va = "top" if dy < 0 else "bottom"
        ax_join.annotate(label, xy=(apex, mz), xytext=(0, dy),
                         textcoords="offset points", ha="center",
                         va=va, fontsize=9.5)

    ax_join.set_title("Joining an existing slot", loc="left", fontweight="bold")
    ax_join.text(
        (rt_lo + rt_hi) / 2, 394.5,
        "one slot, both peptides watched in the same cycle",
        ha="center", fontsize=9.5, color="#475569",
    )

    # Right: opens a new slot.
    ax_open = axes[1]
    apex_a, mz_a, w_a = 7.5, 400.5, 0.4
    apex_b, mz_b, w_b = 10.0, 408.0, 0.4

    for apex, mz, w, colour in [
        (apex_a, mz_a, w_a, PALETTE["slot_a"]),
        (apex_b, mz_b, w_b, PALETTE["slot_b"]),
    ]:
        slot_mz_min = mz - iso / 2
        slot_mz_max = mz + iso / 2
        slot_rt_min = apex - w - pad_rt
        slot_rt_max = apex + w + pad_rt
        ax_open.add_patch(mpatches.Rectangle(
            (slot_rt_min, slot_mz_min),
            slot_rt_max - slot_rt_min,
            slot_mz_max - slot_mz_min,
            facecolor=colour, alpha=0.25, edgecolor=colour, linewidth=1.5,
        ))
        ax_open.plot([apex - w, apex + w], [mz, mz], color="#1e293b", linewidth=2)
        ax_open.scatter(apex, mz, color="#1e293b", s=42, zorder=3)

    ax_open.annotate("Peptide 1", xy=(apex_a, mz_a), xytext=(0, -14),
                     textcoords="offset points", ha="center", va="top",
                     fontsize=9.5)
    ax_open.annotate("Peptide 2", xy=(apex_b, mz_b), xytext=(0, 12),
                     textcoords="offset points", ha="center", va="bottom",
                     fontsize=9.5)
    ax_open.set_title("Opens a fresh slot", loc="left", fontweight="bold")
    ax_open.text(
        (rt_lo + rt_hi) / 2, 394.5,
        "two slots: two separate cycles, more budget consumed",
        ha="center", fontsize=9.5, color="#475569",
    )

    # Use y=1.04 so the suptitle clears the per-axes titles.
    fig.suptitle(
        "MTM scheduling: when a new peptide shares a slot vs. needs its own",
        fontweight="bold", x=0.0, ha="left", y=1.04,
    )
    _save(fig, "join-vs-open-slot")


# Figure 5: time padding around a peak boundary.
def figure_time_padding() -> None:
    fig, ax = plt.subplots(figsize=(8.5, 3.0), constrained_layout=True)

    rt_apex = 20.0
    half_peak = 0.6
    pad = 0.25

    # Padded scheduling window (drawn first so peak draws on top).
    ax.add_patch(mpatches.Rectangle(
        (rt_apex - half_peak - pad, 0.05),
        2 * (half_peak + pad), 1.3,
        facecolor="none", edgecolor=PALETTE["pad_face"],
        linewidth=2, linestyle="--",
        label="scheduling window (peak + drift buffer)",
    ))
    ax.add_patch(mpatches.Rectangle(
        (rt_apex - half_peak, 0.4),
        2 * half_peak, 0.6,
        facecolor=PALETTE["peak_face"], alpha=0.7,
        edgecolor=PALETTE["peak_face"],
        label="measured peak window",
    ))
    ax.scatter(rt_apex, 1.0, marker="v", color="#1e293b",
               s=40, zorder=4, label="peak apex")

    # Annotation arrows under the axis.
    ax.annotate("", xy=(rt_apex - half_peak, -0.10),
                xytext=(rt_apex - half_peak - pad, -0.10),
                arrowprops=dict(arrowstyle="<->", color="#475569"))
    ax.text(rt_apex - half_peak - pad / 2, -0.30,
            f"pad {pad*60:.0f} s",
            ha="center", va="top", fontsize=9.5, color="#475569")

    ax.annotate("", xy=(rt_apex + half_peak, -0.10),
                xytext=(rt_apex + half_peak + pad, -0.10),
                arrowprops=dict(arrowstyle="<->", color="#475569"))
    ax.text(rt_apex + half_peak + pad / 2, -0.30,
            f"pad {pad*60:.0f} s",
            ha="center", va="top", fontsize=9.5, color="#475569")

    ax.set_xlim(rt_apex - 2, rt_apex + 2)
    ax.set_ylim(-0.7, 1.7)
    ax.set_yticks([])
    ax.set_xlabel("retention time (min)")
    ax.set_title(
        "Time padding: extra time on each side of the peak to absorb RT drift",
        loc="left", fontweight="bold",
    )
    ax.legend(loc="upper right", bbox_to_anchor=(1.0, 1.0), ncol=1)

    _save(fig, "time-padding")


def main() -> None:
    figure_two_pass()
    figure_cycle_budget()
    figure_three_objectives()
    figure_join_vs_open()
    figure_time_padding()


if __name__ == "__main__":
    main()
