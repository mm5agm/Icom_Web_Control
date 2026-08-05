# calibration-contributions

Where the meter calibration numbers in `wwwroot/calibration.default.*.json`
actually come from. One file per radio model.

**This is a development artefact, not a shipped asset.** It must never be added
to `wwwroot`, to the `.csproj` publish items, or to `installer.nsi`. Nothing in
an installed IWC reads it.

Full design: [`docs/design/calibration-contributions.md`](../docs/design/calibration-contributions.md).

## Why it exists

The shipped defaults started life as hand-typed guesses. They improve only when
operators send in measurements from real radios. Importing each one straight
into the default file is last-write-wins: the second contributor silently erases
the first, one mis-measured radio becomes everyone's default, and nothing records
where any number came from.

So the default file is **derived**, not edited. Every contribution is kept here,
and the shipped value for each point is the **median** across contributions — an
outlier is outvoted rather than obeyed.

## Privacy

`from` holds a **callsign and nothing else**. Callsigns are public; email
addresses, real names and locations are not, and this directory is committed to
git. If provenance genuinely isn't known, leave `from` out and say so in `note`.

## Fields

| Field | Meaning |
|---|---|
| `placeholders` | Every value-vector the shipped default has ever held, per meter. How a contribution that just echoes the shipped numbers back is recognised as un-measured. Appended to, never pruned. |
| `contributions[].meters` | The numbers as sent, with the point `labels` alongside so a structural change is caught rather than mis-indexed. |
| `contributions[].unmeasured` | Meters this contributor left at the shipped placeholders. Set by the import, not the contributor; excluded from the median. |
| `contributions[].excluded` | Set by hand to drop a contribution without deleting it, then recompute. |

One contribution per callsign per model: re-importing the same operator's file
supersedes their previous numbers rather than giving them a second vote.

## Undoing a bad contribution

Set `"excluded": true` on it (with an `excludedReason`), then click
**↻ Recompute from contributions (dev)** on the Meter Calibration page. The
shipped default falls back to the median of what remains.

## The seeded placeholders

Both files start with zero contributions — the shipped defaults are still the
original hand-typed placeholders, and a recompute against an empty store must
leave them byte-identical.

Each meter lists the current shipped vector first. The second vector, where
present, is the pre-carve Yaesu-era table that `calibration.default.json` held
before the Icom model files were created — seeded defensively so a user file
that predates the carve is still recognised as un-measured. `TPA` is a
Yaesu-only meter with no current vector at all, for the same reason.
