# Screenshot recapture checklist — IWC release (Phase 1 rebrand)

> ## ✅ CLOSED — commit `2c9d443` (2026-07-30)
>
> **24 images re-shot under Icom branding, 8 deleted.** Reconciled box-by-box
> against that commit's file list on 2026-08-02: every 🔴 App-UI shot was
> recaptured, and every 🟠/🗑 entry was resolved by deletion. The boxes below
> now reflect what actually happened — previously they were all left unticked,
> which read as ~30 outstanding shots when the work was already done.
>
> **Two items are genuinely still open** — both *new* shots that never existed,
> not re-captures. They are at the bottom under "Still open".

> Cross-referenced between the images `USER_MANUAL.md` actually embeds and the
> `pictures/` folder, so we only re-shoot what genuinely changed.
>
> Colin captured the app-UI shots himself (main screen differs on the Icom build).

## 🔴 App-UI shots — recaptured on the Icom build (24)

IWC's own screens, which changed for the IC-7300 build. All modified in `2c9d443`:

**Main / About / tray**
- [x] `AboutPage.png` — now shows IC-7300 MkII firmware
- [x] `SystemTrayIcon.png`
- [x] `DevelopScreen.png`

**Operating panels**
- [x] `CW-Keyer.png`
- [x] `FM-Repeater.png`
- [x] `Vox-Control.png`
- [x] `Calibration.png` — recaptured in `2c9d443`, but referenced by nothing
      until 2026-08-02, when it was embedded in manual §10. Worth remembering
      that "recaptured" and "actually used" are two different questions.

**DX**
- [x] `DX-Watch.png`
- [x] `DX-Spots-All-Bands.png`
- [x] `DX-Spots-Single-Band.png`
- [x] `DX-Alert-PopUp.png`
- [x] `Screen popups.png` _(manual references this via `Screen%20popups.png` —
      the space is URL-encoded, so a naive link-checker reports it missing)_

**Memories (8)**
- [x] `Memories_ADIF_Import`
- [x] `Memories_Banks_Bar`
- [x] `Memories_Create_Themed_Banks_Dialog`
- [x] `Memories_Editor_Page`
- [x] `Memories_Floating_Panel`
- [x] `Memories_Save_To_Mem_Button`
- [x] `Memories_Starter_Bank_Loaded`
- [x] `Memories_Tile_Closeup`

**Settings (3)**
- [x] `Settings_Network_Config`
- [x] `Settings_Restart_Required`
- [x] `Settings_Test_Cluster`

## 🟠 Yaesu-specific — all resolved by deletion, none replaced

- [x] `ftdx101mp.jpg` — **deleted**, not replaced. The manual embeds no rig
      photo at all now. `pictures/icom-ic-7300MK2-2.jpg` and
      `ico-ic-7300_it_xl.jpg` are on disk but referenced by nothing; the
      MK2 one is kept deliberately as the colour source for the front-panel
      skin (Phase 7), not as a manual asset.
- [x] `ftdx101mp_back.jpg` — **deleted**, not replaced.
- [x] `Yaesu_Enhanced_USB_Properties.png` — **deleted**. This box allowed
      "drop it if no longer relevant", and that is what happened: the IC-7300's
      USB is a Silicon Labs CP210x and needs no equivalent walkthrough.
- [x] `WSJT-X_Radio.png` — recaptured with the Icom selection in the dropdown.

## 🟢 Leave as-is — third-party config, unaffected by the rebrand (~20)

Other apps' own screens; no IWC branding to change:
`Log4OM_*` (14 after `Log4OM_UDP_Proxy` was deleted), `Gridtracker_*` (2),
`JTAlert_Settings_For_Log4OM`, `WSJT-X_Reporting_UDP.png`,
`GitHubCreateIssue.png`, `SmartAppControl.png`.

## 🗑 Orphans — all deleted in `2c9d443`

- [x] `Spectrum_New.png` — superseded by Side_By_Side / Stacked
- [x] `Spectrum_Side_By_Side.png` — dual-SDR era; manual no longer references it
- [x] `Spectrum_Stacked.png` — dual-SDR era; manual no longer references it
- [x] `Settings_SDR_Warning.png` — SDR Settings section removed for the IC-7300 build
- [x] `Log4OM_UDP_Proxy.png`

---

## Still open

Neither is a re-capture — both are shots that have never existed.

- [ ] **A CI-V band-scope screenshot for manual §5.4.** §5.4 (Spectrum Display)
      currently has no image at all: the dual-SDR shots were deleted with the
      SDR prose and nothing replaced them, so the manual describes the
      IC-7300's built-in scope in words only. This is the most visually
      distinctive thing IWC does and the section showing it is the one section
      with nothing to look at.
- [ ] **Decide whether the manual wants a rig photo at all.** The Yaesu front
      and back photos were deleted rather than replaced. If §1 or §2 should
      show the radio, an IC-7300 MkII photo needs shooting *and* embedding;
      if not, this is closed as "deliberately no rig photo" and can be struck.
