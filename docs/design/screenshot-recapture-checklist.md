# Screenshot recapture checklist — IWC release (Phase 1 rebrand)

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
- [ ] `Spectrum_Side_By_Side.png`
- [ ] `Spectrum_Stacked.png`

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

**Settings (4)**
- [ ] `Settings_Network_Config`
- [ ] `Settings_Restart_Required`
- [ ] `Settings_SDR_Warning` — ⚠ **check first:** IC-7300 uses CI-V scope, not an
      SDR, so this Settings section may no longer exist. Don't shoot it until
      confirmed it's still in the UI.
- [ ] `Settings_Test_Cluster`

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
- [ ] `Log4OM_UDP_Proxy.png`
