# Design — calibration contributions store

**Status:** Proposed — not implemented
**Author:** Colin (MM5AGM), with implementation planning support
**Date:** 2026-08-01
**Supersedes behaviour in:** `Services/CalibrationStorage.cs` — `ApplyIntoDefault`

---

## Context

IWC ships a placeholder calibration table per radio model (`wwwroot/calibration.default.IC-7300MK2.json`). It maps a raw meter value the radio reports over CI-V (0–255) to a real-world reading — watts, S-points, amps. The shipped numbers were typed by hand as a starting point; they are not measurements.

Users improve them. The Meter Calibration page has an **✉ Email calibration to developer** button, and a dev-only import that folds a received calibration into the shipped file with minimal-diff surgery so the change lands as a small, reviewable git diff.

The import is **last-write-wins per value** ([`CalibrationStorage.cs:242-259`](../../Services/CalibrationStorage.cs#L242-L259)):

```csharp
for (int j = 0; j < cur.Points.Count; j++)
    if (cur.Points[j].Raw != inc.Points[j].Raw)
        changes[j] = inc.Points[j].Raw;
```

Import Ann then Bob, and the file holds Bob's numbers wherever they disagree. Nothing records that Ann contributed at all.

## Problems with last-write-wins

### 1. Un-measured values are indistinguishable from measured ones

`EnsureUserCalibrationExists` seeds a new install's `calibration.user.json` by **copying the shipped default**, and never re-seeds it afterwards — upgrades keep the user's file. So every export carries a full set of values for all seven meters whether or not the user measured any of them.

A contributor who calibrated only the S-meter still sends PWR values. Those are whatever placeholder they were seeded with, and if they installed an earlier release, they are an *older* placeholder than the one now in the repo. Importing them silently reverts an earlier contributor's real measurement, and the status line reports it as `updated PWR (1)` — indistinguishable from a genuine improvement.

The git diff is the only safety net, and it requires noticing that a line reverting to an older value is not what the sender meant to say.

### 2. The table can only ever be as good as the last contributor

Two people measure 100 W on the PWR meter and get raw 224 and 228. The table records whichever arrived second. There is no way to express "the answer is somewhere in the middle", and no way to see that two people even disagreed.

### 3. A bad contribution is unrecoverable

Someone measures power on an uncalibrated SWR meter and sends a skewed table. Once imported, the previous values are gone from everything except git history. Recovering means finding the pre-import commit by hand.

### 4. A running mean would fix (2) but make (1) and (3) worse

The obvious fix is an incremental mean — store a count `n` per meter and update `newMean = (oldMean × n + newReading) / (n + 1)`. The arithmetic is correct and it does solve the "only the last contributor counts" problem.

But it makes the other two problems worse, not better:

- Un-measured values get folded in as if they were measurements, dragging the mean toward a stale placeholder — and unlike last-write-wins, the result is always a plausible-looking small change rather than a suspicious revert. Averaging makes seed-detection *more* necessary, not less.
- The individual readings are gone, so a bad contribution can never be removed. You can't un-average.
- Two or three contributions is a small sample, and one outlier moves a mean a long way. A median would be far more robust — and you cannot take a median of a running average.
- `n` has nowhere to live. It can't go in the shipped default, because that file is copied wholesale to seed every user's calibration; the counts would land in user files and come back in their exports.

Once a separate dev-side file is needed anyway, storing the **readings** rather than a running count costs a few KB and solves all three problems.

## Decision

Keep the individual contributions in a dev-side store, and **derive** the shipped default from them.

The shipped default file format does not change — it stays exactly what it is today, so users, the installer, and `EnsureUserCalibrationExists` are unaffected. What changes is where its numbers come from: instead of being edited in place by each import, it is recomputed from the contributions store and written back with the same minimal-diff surgery already in use.

### Data model

One file per radio model, at the repo root in `calibration-contributions/`:

```
calibration-contributions/
  IC-7300MK2.json
  IC-7300.json
```

**This directory must never be added to `wwwroot`, the `.csproj` publish items, or `installer.nsi`.** It is a development artefact tracked in git, not a shipped asset. `KnownDefaults()` scans `wwwroot` only, so it is unaffected.

```jsonc
{
  "model": "IC-7300MK2",
  // Every value-vector the shipped default has ever held, per meter. Used to
  // recognise un-measured values echoed back from a seeded user file. Appended
  // to on every recompute; never pruned.
  "placeholders": {
    "PWR":     [[0,22,83,142,182,225]],
    "S-Meter": [[0,4,30,65,95,131,171,208,255]]
  },
  "contributions": [
    {
      "id": "2026-08-01-mm5agm",
      "from": "MM5AGM",             // callsign only — see Privacy
      "date": "2026-08-01",
      "appVersion": "1.0.0",
      "note": "Bird 43 with 100W slug, dummy load",
      "meters": {
        "PWR":     { "labels": ["0","5","25","50","75","100"],
                     "raw":    [0, 22, 83, 142, 182, 226] },
        "S-Meter": { "labels": ["S1","S3","S5","S7","S9","+20","+40","+60"],
                     "raw":    [0, 4, 30, 65, 95, 131, 171, 254] }
      },
      // Set by the import, not by the contributor. Meters whose values matched
      // a known placeholder exactly — recorded for the audit trail, excluded
      // from aggregation.
      "unmeasured": ["SWR", "Compression", "ALC", "IDD", "VPA"],
      // Set by hand to drop a contribution without deleting it.
      "excluded": false,
      "excludedReason": null
    }
  ]
}
```

`labels` is stored alongside `raw` so a structural change (a meter gaining or losing a calibration point) is detectable per contribution rather than silently mis-indexed.

### Seed detection

A contributed meter is treated as **un-measured** when its `raw` vector exactly equals any vector in `placeholders[meterName]`.

This needs no change to the export format and works retroactively on calibrations from any release, because `placeholders` accumulates every value-vector the shipped file has ever had. Seed the initial list from `git log -p` on the default file.

A genuine measurement could in principle coincide exactly with a placeholder across all six or nine points. The probability is negligible, and the consequence is only that one contribution to one meter is skipped — the failure direction is safe.

*Optional later improvement:* have `EnsureUserCalibrationExists` stamp `seededFrom: { version, sha256 }` into the user file at seed time, and have the export carry it. That makes detection exact rather than inferential, but only for installs from that release onward, so the placeholder-vector test is needed regardless.

### Aggregation

Per meter, per point index, over all contributions that are not `excluded` and do not list that meter in `unmeasured`:

| Contributions | Result |
|---|---|
| 0 | keep the existing hand-authored placeholder unchanged |
| 1 | use it as-is |
| ≥ 2 | **median** per point |

Median, not mean: at these sample sizes one bad contribution moves a mean a long way and a median shrugs it off. For an even count, take the mean of the two middle values.

Round to the nearest integer. Raw values are what the radio actually reports over CI-V — integers in 0–255 — so a fractional entry in the table is noise. (`FormatRaw` already writes whole doubles without a decimal point, so this only affects tidiness, not correctness.)

### Validation before writing

Two checks, both blocking:

1. **Monotonicity.** `CalibrateNumeric` interpolates between points ordered by `raw`, and `LoadFromPath` sorts by `raw`. An aggregated vector that is not strictly increasing would silently reorder the labels and produce a nonsense table. If the median result is non-monotonic, refuse the recompute for that meter, keep the previous values, and report which points collide.

2. **Structural agreement.** A contribution whose `labels` differ from the shipped default's is excluded from aggregation for that meter and reported — the existing `structural` list in `CalibrationImportResult` already carries this concept.

### Spread reporting

Report per point: `n`, min, max, and the chosen median. Flag any point whose max − min exceeds a threshold (10 raw counts is a reasonable starting value) as "contributors disagree".

This is the part that makes the store worth having day to day. It answers "does this table rest on two people who agree, or five who don't?" — which is far more informative than the aggregate number alone.

## Workflow

**Receiving a calibration.** Unchanged for the user: they calibrate, click ✉, the JSON arrives by email. Colin copies it and clicks the existing dev import button. The import now:

1. Parses the JSON as today.
2. Detects the model as today.
3. Flags un-measured meters against `placeholders`.
4. Appends a contribution entry, prompting for callsign / note / id.
5. Recomputes every meter from the store.
6. Writes the shipped default with the existing minimal-diff surgery.
7. Reports what changed, with the spread summary.

**His own bench calibration.** The `POST /api/calibration/import-default/current` path added in `3122f6d` feeds the same pipeline from the saved file, no clipboard involved.

**Dropping a bad contribution.** Set `"excluded": true` with a reason in the JSON, then `POST /api/calibration/contributions/recompute`. The shipped table is rebuilt from what remains. Nothing is lost, and the reason is in the file for whoever reads it next.

**Regenerating from scratch.** The recompute is deterministic and total — the shipped default is a pure function of the contributions store plus the hand-authored placeholder. That property is what makes exclusion reversible, and it is worth preserving in any later change.

## Privacy

The exported calibration JSON contains no identifying information, and this design does not add any. The `from` field is filled in by Colin at import time and should hold a **callsign only** — public information in amateur radio.

Do not put email addresses, real names, or locations in the contributions file. It is committed to git; the repo is private today but may not always be.

## Implementation

**Built 2026-08-05.** Steps 1–7 are done; step 8 stays open on purpose.

1. ✅ `Models/Calibration/CalibrationContributions.cs` — the store's model classes.
2. ✅ `Services/CalibrationContributionsStore.cs` — load/save, `Record`, `Recompute` returning per-meter aggregated vectors plus the spread report. Pure over the store, no file surgery.
3. ✅ `CalibrationStorage.ApplyIntoDefault` — surgery untouched; the values it writes now come from `Recompute` rather than straight from the incoming file.
4. ✅ `CalibrationImportResult` — gained `Unmeasured`, `Refused`, `Spread` and `Contributors`.
5. ✅ `CalibrationController` — `POST /api/calibration/contributions/recompute`, dev-gated like the others.
6. ✅ Meter Calibration page — callsign/note inputs and a **↻ Recompute from contributions (dev)** button inside the existing `@if (Model.IsDevelopmentMode)`; the spread and refusal detail go to the status tooltip and the console.
7. ✅ `calibration-contributions/IC-7300.json` and `IC-7300MK2.json` seeded from the default files' git history — the current vectors, plus the pre-carve Yaesu-era ones `calibration.default.json` held at `e214517` (including `TPA`, which no Icom table has) so a user file predating the carve is still recognised as un-measured.
8. ⬜ **Deliberately still empty.** Colin's bench calibration is not recorded, because there is nothing to record: the 226 / 254 values reverted in `3122f6d` were test mutations, **not** measurements. The shipped tables are still the original hand-typed placeholders, and a recompute against the empty store leaves them byte-identical — which is exactly the property that proves the wiring is right. The first real contribution is the first one that moves a number.

Decisions taken during implementation, beyond what is above:

- **One contribution per callsign per model.** Re-importing an operator's file supersedes their previous numbers instead of appending. Without this, Colin promoting his own bench calibration twice would give himself two votes in the median. Anonymous contributions can't be matched up, so they always append.
- **Un-measured is decided once, at import**, against the placeholders known then — never re-derived during a recompute. Re-deriving would discard the very contribution that produced the current shipped value the moment that value became a placeholder.
- **Raw values outside 0–255 are refused**, alongside the non-monotonic check: the CI-V meter range is a byte, so anything else is a transcription error.
- **The store file is written with scalar arrays collapsed onto one line.** `WriteIndented` alone turns a six-point vector into six lines; this file exists to be read in a git diff.

Verified against the real classes, without hardware: empty store recommends nothing; an ALC-only contributor has their other six meters flagged un-measured; a re-import by the same callsign keeps the contribution count at one; a wild third contributor is outvoted by the median and shows up in the spread report; a non-monotonic vector is refused without affecting other meters; excluding the wild contributor moves the median back.

## Consequences

**Positive**

- A contributor's work is never silently reverted by someone else's stale seed values.
- One bad contribution is reversible, by hand, with the reason recorded.
- The table's confidence is visible: `n` per point and the spread between contributors.
- Median aggregation is robust at the sample sizes this will realistically see.
- The shipped file format, the installer, and every user-facing path are untouched.

**Negative**

- More moving parts than in-place surgery: a store, an aggregation step, and a validation step where there used to be one regex pass.
- The store must be kept in sync with the shipped file by hand if anyone ever edits the default directly. Editing the default directly becomes something to avoid — the recompute would overwrite it at the next import.
- `placeholders` grows without bound. It is a few hundred bytes per entry; not a real concern, but it is never pruned by design, because pruning would break retroactive seed detection.

**Out of scope**

- Any change to what users see or send. The export format stays as-is.
- Per-band or per-power-range calibration. The table is a single curve per meter today and this does not change that.
- Automated collection of any kind. Contributions arrive by email and are entered deliberately.

## Related

- [`iwc-clone-split-plan.md`](iwc-clone-split-plan.md) — the phased carve this sits alongside
- Commit `3122f6d` — the stale-cache, clipboard and Yaesu-defaults fixes that preceded this, and the end-to-end verification of the minimal-diff surgery this design keeps
