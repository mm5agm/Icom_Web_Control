# Screenshot recapture checklist — IWC release (Phase 1 rebrand)

> ## ✅ DONE — closed by commit `2c9d443` (2026-07-30)
>
> 24 app-UI images were re-shot under the Icom branding, the Yaesu hardware
> photos and the dead SDR/Log4OM orphans were deleted, and the manual's
> "every screenshot is still the Yaesu build" caveat was removed with them.
> Verified 2026-08-02: `pictures/` contains no `ftdx101mp*`, `Yaesu_*`,
> `Spectrum_*` or `Settings_SDR_Warning` files, and every image the manual
> references exists. The unticked boxes below are kept as the historical
> worklist — **do not treat them as outstanding.**

> Cross-referenced between the images `USER_MANUAL.md` actually embeds and the
> `pictures/` folder, so we only re-shoot what genuinely changed. Every image the
> manual references currently exists (no broken links) — the work is **replacing
> content**, not fixing missing files.
>
> Colin is capturing the app-UI shots himself (main screen differs on the Icom build).

## 🔴 App-UI shots to re-capture on the Icom build (~25)

IWC's own screens, which have changed for the IC-7300 build:

**Main / About / tray**
- [ ] `AboutPage.png` — now shows IC-7300 MkII firmware
- [ ] `SystemTrayIcon.png`
- [ ] `DevelopScreen.png`

**Spectrum**
- The old dual-SDR spectrum shots (`Spectrum_Side_By_Side.png`, `Spectrum_Stacked.png`)
  are **no longer referenced** by the manual — the SDR sections were replaced with
  the IC-7300's built-in CI-V scope prose. Listed under 🗑 Orphans below.
- [ ] _(new)_ If the IWC build renders a CI-V scope panel worth showing, shoot a
      fresh single-scope screenshot and add it to §5.4 of the manual.

**Operating panels**
- [ ] `CW-Keyer.png`
- [ ] `FM-Repeater.png`
- [ ] `Vox-Control.png`
- [ ] `Calibration.png`

**DX**
- [ ] `DX-Watch.png`
- [ ] `DX-Spots-All-Bands.png`
- [ ] `DX-Spots-Single-Band.png`
- [ ] `DX-Alert-PopUp.png`
- [ ] `Screen popups.png` _(manual references this via `Screen%20popups.png`)_

**Memories (8)**
- [ ] `Memories_ADIF_Import`
- [ ] `Memories_Banks_Bar`
- [ ] `Memories_Create_Themed_Banks_Dialog`
- [ ] `Memories_Editor_Page`
- [ ] `Memories_Floating_Panel`
- [ ] `Memories_Save_To_Mem_Button`
- [ ] `Memories_Starter_Bank_Loaded`
- [ ] `Memories_Tile_Closeup`

**Settings (3)**
- [ ] `Settings_Network_Config`
- [ ] `Settings_Restart_Required`
- [ ] `Settings_Test_Cluster`
- `Settings_SDR_Warning` — **no longer referenced** by the manual (the SDR Settings
  content was removed for the CI-V-scope IC-7300 build). Listed under 🗑 Orphans below.

## 🟠 Yaesu-specific — must be *replaced*, not just re-shot (4)

- [ ] `ftdx101mp.jpg` → **IC-7300 MkII** front photo
- [ ] `ftdx101mp_back.jpg` → **IC-7300 MkII** back photo
- [ ] `Yaesu_Enhanced_USB_Properties.png` → Icom's **Silicon Labs CP210x** COM-port
      properties (or drop it if no longer relevant)
- [ ] `WSJT-X_Radio.png` — third-party, but it *names the rig* in the dropdown, so
      re-shoot with the Icom selection

## 🟢 Leave as-is — third-party config, unaffected by the rebrand (~20)

Other apps' own screens; no IWC branding to change:
`Log4OM_*` (15), `Gridtracker_*` (2), `JTAlert_Settings_For_Log4OM`,
`WSJT-X_Reporting_UDP.png`, `GitHubCreateIssue.png`, `SmartAppControl.png`.

## 🗑 Orphans to delete (in `pictures/`, not referenced by the manual)

- [ ] `Spectrum_New.png` — superseded by Side_By_Side / Stacked
- [ ] `Spectrum_Side_By_Side.png` — dual-SDR era; manual no longer references it
- [ ] `Spectrum_Stacked.png` — dual-SDR era; manual no longer references it
- [ ] `Settings_SDR_Warning.png` — SDR Settings section removed for the IC-7300 build
- [ ] `Log4OM_UDP_Proxy.png`
