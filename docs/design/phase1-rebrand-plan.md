# Phase 1 rebrand plan — strip YWC/Yaesu, ship as IWC

> **Goal:** a releasable IWC with **no YWC or Yaesu names/content on any
> user-facing or shipped surface**, a User Guide that matches the running Icom
> build, and the version lineage reset to the `0.1.0-alpha` line.
>
> **Scope snapshot (re-derived 2026-07-30, `git grep` counts):**
> - `YWC|Yaesu|Yaesu_Web_Control` → **352 hits across 48 files**
> - `FTdx|FTDX|ftdx|101MP` → **307 hits across 46 files**
>
> Not every hit must change. The rule is **surface-based**: anything a user
> sees or that ships in the installer must be Icom-clean; internal identifiers
> are renamed for hygiene but aren't release-blocking; a few docs intentionally
> record the YWC origin and should **stay**.

## ⚠ Not a find-and-replace

Three different kinds of change hide in these counts — do **not** bulk-sed:

1. **Mechanical renames** — `YWC`→`IWC`, `Yaesu Web Control`→`Icom Web Control`
   in visible text. Safe.
2. **Semantic data work** — Yaesu band/filter/calibration data files need
   **real IC-7300 MkII values**, not a renamed Yaesu table. Renaming alone
   ships wrong data. (See §D.)
3. **Live-vs-dead audit** — several Yaesu-CAT classes may be orphaned by the
   CI-V carve. Decide delete-vs-keep per file before touching them. (See §E.)

Also **keep** the deliberate origin references: `CLAUDE.md` banner,
`docs/design/iwc-clone-split-plan.md`, `docs/decisions/*` — these document that
IWC was carved from YWC and are correct as-is.

---

## A. Version / identity reset  (release-blocking)

- [ ] `Icom_Web_Control.csproj` — `<Version>1.5.6</Version>` (+ AssemblyVersion/
      FileVersion, lines ~86-88) still carry YWC's lineage. Reconcile to the
      `0.1.0-alpha` line so csproj matches `Models/AppVersion.cs`
      (`Current = "0.1.0-alpha"`).
- [ ] `Models/AppVersion.cs` — already reset (IC-7300 MkII firmware block). Verify.
- [ ] `%APPDATA%` path: CLAUDE.md documents `%APPDATA%\MM5AGM\Yaesu Web Control\`
      for settings + `radio_state.json`. Decide whether IWC keeps writing under a
      **Yaesu Web Control** folder (user-visible on disk). If it should be
      `Icom Web Control`, find where the folder name is set and change it — and
      note there's **no migration** of old state (fresh alpha, acceptable).

## B. User-facing UI  (release-blocking)

Visible strings, titles, headings, tooltips, alt-text:

- [ ] `Pages/Index.cshtml` (7 YWC + 18 FTdx hits) — main control panel; the
      highest-visibility surface.
- [ ] `Pages/Settings.cshtml` (+ `.cshtml.cs`)
- [ ] `Pages/About.cshtml` (+ `.cshtml.cs`)
- [ ] `Pages/MeterCalibration/Index.cshtml`
- [ ] `wwwroot/js/ui/site.js` (2 YWC + 20 FTdx) — check for user-visible strings
      vs internal identifiers.
- [ ] `wwwroot/js/sdr/spectrum-panel.js`
- [ ] Browser tab titles / `<title>` / page `<h1>`s across all Pages.
- [ ] `localStorage` keys namespaced `ywc.*` (e.g. `ywc.spectrumMode` per
      CLAUDE.md) — renaming changes the key, silently resetting the user's saved
      toggle. Fresh alpha, no users → safe to rename now; decide and note it.

## C. JS module filenames + identifiers  (hygiene; do carefully)

These carry the Yaesu model in the **filename**, imported by path elsewhere:
- [ ] `wwwroot/js/orchestrators/FTdx101Meters.js`
- [ ] `wwwroot/js/calibration/FTdx101Calibration.js`
- [ ] `wwwroot/js/ui/filter-scope-panel.js` (17 FTdx hits — mostly identifiers)
- [ ] `wwwroot/js/ui/if-width-tables.js` (9 hits)
- [ ] `wwwroot/js/calibration/calibration-tables.js`
- Rename files **and** every `import`/`<script src>` that references them, or the
  page breaks. Grep the new name back to confirm zero stale references.

## D. Data files — need real IC-7300 MkII values  (semantic, not rename)

- [ ] `wwwroot/data/audio-filter-ex-map.json` (4 FTdx hits) — Yaesu IF/filter map;
      needs Icom filter widths or removal if IWC doesn't use it.
- [ ] Band-plan / starter-bank / calibration default JSON (Yaesu-derived) — verify
      each against IC-7300 MkII (HF+6m(+4m EU), single RX) rather than inheriting
      Yaesu band edges / meter scaling.
- [ ] `wwwroot/js/ui/if-width-tables.js` — IF width tables are radio-specific.

## E. Live-vs-dead Yaesu-CAT audit  (decide delete vs keep)

The CI-V carve may have orphaned these. For each: is it still wired into DI /
`Program.cs` / a live code path? Delete if dead; port/rename if live.
- [ ] `Services/CatMessageDispatcher.cs` (12 FTdx hits) — Yaesu FA/FB parser.
- [ ] `Controllers/CatController.cs` (33 FTdx hits) — big surface; check routes.
- [ ] `Services/Ftdx3000Roofing.cs` (3 hits) — Yaesu roofing filters.
- [ ] `Services/RadioCapabilities.cs` (8 hits)
- [ ] `Services/AudioFilterMapService.cs` (3 hits)
- [ ] `Services/CatCommands.cs`, `Services/CatMultiplexerService.cs` — CAT-vs-CIV.
- [ ] `Services/MeterPollingService.cs` (3 YWC + 5 FTdx) — is this the live poll
      path, or has `CivRadioController` superseded it?

## F. Docs shipped to / read by users  (release-blocking for the guide)

- [ ] `USER_MANUAL.md` — **165 YWC/Yaesu + 65 FTdx hits.** Effectively a full
      Icom rewrite. Pair with the screenshot recapture (see
      `docs/design/screenshot-recapture-checklist.md`). Update the voice-commands
      section to current grammar while here (memory: `iwc-usermanual-voice-todo`).
- [ ] `README.md` (7 YWC + refs) — project blurb, badges, release notes,
      per-release download badge version.
- [ ] `VOICE_CONTROL.md`, `CALIBRATION.md`, `CHANGELOG.md` — user-adjacent.
- [ ] `.github/ISSUE_TEMPLATE/*.md` — visible to anyone filing an issue.

## G. Voice pack + assets

- [ ] `wwwroot/voice-packs/YWC-VoicePack-en-US-v1.zip` — filename carries YWC.
      Rename → confirm whatever loads voice packs finds it by the new name (or
      accepts any `*.zip` in the folder). Content is fine (recently refreshed).
- [ ] App icon / tray icon / installer branding — `installer.nsi` is already
      Icom-branded; verify the icon asset itself isn't the Yaesu one.

## H. Internal / leave-as-is (record, don't change)

- `CLAUDE.md` banner, `docs/design/iwc-clone-split-plan.md`, `docs/decisions/*`,
  `Audit.md`, `Plan.md`, `.claude/*` — intentionally reference the YWC origin or
  are dev-only. Not shipped to users. Leave.

---

## Verify

1. `dotnet build Icom_Web_Control.csproj -t:Compile -clp:ErrorsOnly` after each
   batch (compile-only avoids the exe-lock while the app runs).
2. Run the app, click through every page — no "YWC"/"Yaesu"/"FTdx" visible.
3. `git grep -iE "ywc|yaesu|ftdx|101mp"` and confirm the only survivors are the
   §H intentional-origin files.
4. Voice smoke-test still loads the (renamed) pack; spectrum + calibration pages
   still function (filename renames didn't break imports).
5. Full self-contained publish → confirm the installer payload is Icom-clean.

## Restore point

Started from clean commit **`a276332`** (develop). Any rebrand batch is
revertible with `git reset --hard a276332` or by dropping individual commits.
