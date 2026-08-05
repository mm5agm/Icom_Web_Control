# FTdx101MP CAT Capability Audit

*Implemented (63 opcodes found): FA, FB, FT, VS, MD, TX, PC, PS, AN, RF, RU, GT, PA, RA, NR, RL, NB, NL, BC, BP, SH, IS, AG, RG, SQ, MG, PR, PL, ML, RT, XT, CF, RC, RD, CO, ST, SV, AC, KP, KS, BI, SD, VX, VG, VD, RS, RO, CT, CN, SM, RM, MS, ID, IF, DT, AI, VT, ZI, KY, EX (partial), AB/BA (approximated via FA/FB read-write, not the atomic CAT command).*

---

## RF Controls

- **Band Up**
  - `BU;`
  - Steps the active VFO to the next higher amateur band. Remote operators have no hardware VFO band knob and currently have to type a frequency manually to change bands.
  - Priority 2

- **Band Down**
  - `BD;`
  - Same as BU in the opposite direction. Needed alongside BU.
  - Priority 2

- **TX Narrow**
  - `XN{P1};` — 0 = Normal, 1 = Narrow
  - Selects normal vs narrow TX bandwidth. Narrow is standard for digital modes and SSB in congested contest conditions. Currently not exposed so users must use the front panel.
  - Priority 3

---

## Audio Controls

- **Anti-VOX Gain**
  - `VA{P1}{000-100};`
  - Controls how much speaker audio bleeds into the microphone circuit before VOX triggers. With VOX enabled and no anti-VOX control in the UI, operators who hear speaker bleed-through are forced to adjust from the front panel mid-QSO.
  - Priority 2

- **EX — uncovered menu parameters (beep, backlight, display, meter ballistics, SSB carrier, etc.)**
  - `EX{menu-id}…;` — approximately 130+ individual menu entries
  - The EX command is already used for Audio Filter (LCUT/HCUT per mode). Many other EX-addressable menu items remain unreachable: beep level, LCD brightness, menu scroll speed, semi-BK anti-VOX, and dozens of per-mode adjustments. Most users never need these, but power users and accessibility users who operate the radio remotely want full menu access without touching the front panel.
  - Priority 5

---

## DSP / Filtering

- **Keyer Type**
  - `KI{P1};` — 0 = Single Paddle, 1 = Paddle-A (Iambic A), 2 = Paddle-B (Iambic B), 3 = Bug
  - Operators set their keyer mode once when they hook up their paddle and rarely change it, but cannot do so via YWC today. Iambic A/B confusion is a common setup question.
  - Priority 3

- **Keyer Memory Read**
  - `KM{P1};` — P1 = memory number 1–5
  - Reads back the contents of a stored CW memory message. YWC can send KY to write memory content but cannot read it back, so the UI can never verify or display what is currently stored.
  - Priority 5

- **Keyer Memory Clear**
  - `KC{P1};` — P1 = memory number
  - Clears a single CW memory slot. Currently the only way to clear a message from the UI is to overwrite it with a space, which is a workaround.
  - Priority 5

---

## VFO / Clarifier

- **VFO Step Up**
  - `UP;`
  - Advances the active VFO frequency by the currently-selected tuning step. Operators who remote-operate and want to nudge the VFO by a small increment without typing a whole frequency currently have no YWC control for this.
  - Priority 2

- **VFO Step Down**
  - `DN;`
  - Same as UP in the opposite direction. Pair with UP and TS.
  - Priority 2

- **Tuning Step Select**
  - `TS{P1}{P2P2P2}{P3};` — P1 = VFO (0=A, 1=B), P2 = step code (0–16 maps to 1 Hz–1 MHz steps), P3 = 0 fixed
  - Sets the step size used by UP/DN. Without TS, UP/DN would use whatever step the radio last had selected from the front panel, which may be wrong. All three (UP, DN, TS) need to land together to be useful.
  - Priority 2

- **VFO A → VFO B Copy (atomic)**
  - `AB;`
  - Copies VFO A frequency and mode to VFO B atomically. The app achieves the same result by reading FB; and writing FA; but uses two round-trips rather than one, which introduces a brief inconsistency window. For normal use this is invisible; for fast S&P operating or macro scripting it matters.
  - Priority 5

- **VFO B → VFO A Copy (atomic)**
  - `BA;`
  - Same rationale as AB; in the reverse direction.
  - Priority 5

- **Memory Channel Step**
  - `CH{P1};` — 0 = step down, 1 = step up
  - Increments/decrements the active memory channel. Without this, the only way to browse memory channels over CAT is to issue individual MC recalls, which requires knowing the channel number. CH is needed for a "next/previous memory" UX.
  - Priority 4

- **Memory Channel Recall**
  - `MC{P1}{channel};` — P1 = VFO (0=A, 1=B), channel = 001–117
  - Recalls a specific memory channel number to VFO A or B. Distinct from MR (which reads the data) — MC actually tunes the radio to it.
  - Priority 4

---

## TX Controls

*(TX toggle, RF Power, ATU, Split, Speech Processor, Mic Gain, Processor Level, and Monitor are all already implemented.)*

- **Playback Recorder**
  - `PB{P1};` — 0 = stop, 1 = play, 2 = record
  - Controls the FTdx101MP's built-in audio recorder. Used by some operators to review received signals or record a transmission for review. Not a common everyday control but frequently requested by contest operators reviewing their pile-up runs.
  - Priority 5

---

## RX Controls

*(AGC, preamp, ATT, NR, NB, APF, IF Width, Twin PBT, RF Gain, AF Gain,
Squelch are all implemented. Contour and IF Shift are **not** — both are
Yaesu controls with no IC-7300 equivalent; the radio offers Twin PBT and the
manual notch instead, and those have their own endpoints.)*

- **Busy / Squelch Open Status**
  - `BY;` → `BY{P1};` — 0 = not busy, 1 = busy (squelch open)
  - Returns whether the squelch is currently open. Useful for automation (e.g., "wait until channel is clear before transmitting"), accessibility scripts, and remote operation where the operator cannot hear the speaker. Not an everyday control but fills a gap for scripted or automated operation.
  - Priority 5

---

## Metering

*(SM S-meter, RM multi-meter, MS meter type selection, TX status polling are all implemented. This category has no meaningful gaps.)*

---

## Memory / Scanning

- **Memory Channel Read (full data)**
  - `MR{P1}{channel}{frequency}{mode}{…};`
  - Returns the complete stored data for a memory channel. If MR is not fully handled in the memory service, the memory import feature may be reading memories via IF polling rather than direct MR reads, which would be less efficient and may miss per-channel fields.
  - Priority 4

- **Memory Channel Write (full data)**
  - `MW{P1}{channel}{frequency}{mode}{…};`
  - Writes a complete memory channel record. If approximated, the write path may not set all fields (e.g., memory name, per-channel tone settings).
  - Priority 4

- **Memory to VFO A**
  - `MA{channel};`
  - Copies a stored memory channel into VFO A without switching to memory mode. Useful for "tune to this memory then operate VFO" workflows common in DX and contest work.
  - Priority 4

- **Memory to VFO B**
  - `MB{channel};`
  - Same as MA for VFO B.
  - Priority 4

- **Scan**
  - `SC{P1};` — 0 = stop, 1 = scan up, 2 = scan down, 3 = MHz scan up, 4 = MHz scan down, 5 = programmed scan up, 6 = programmed scan down, 7 = memory scan, 8 = select memory scan, 9 = memory scan repeat
  - Starts and stops the radio's built-in scanning modes. No YWC control currently exists for any scan function. Memory scan is the most-requested of these; band scan is a secondary ask.
  - Priority 4

---

## Advanced / Rarely Used

- **Band Scope Source**
  - `BS{P1};`
  - Selects which antenna is fed to the band-scope input. Only relevant to operators using the FTdx101MP's optional band-scope accessory. Rarely needed via CAT.
  - Priority 5

- **Power-save / Standby (write)**
  - `PS0;`
  - Writing PS0; puts the radio into standby mode remotely. PS is already in the implementation but only polled (read), not written. Some remote-station operators want a software "shut down radio" button.
  - Priority 5

---

## Summary by Priority

| Priority | Count | Items |
|----------|-------|-------|
| 2 — Operating controls | 6 | BU, BD, UP, DN, TS, VA |
| 3 — DSP / filtering | 2 | XN, KI |
| 4 — Memory / scanning | 7 | SC, MC, MA, MB, MR, MW, CH |
| 5 — Advanced / rarely used | 9 | AB, BA, BY, PB, KC, KM, BS, PS write, EX remainder |

No Priority 1 gaps exist — all everyday receive-chain controls (RF Gain, AF Gain, AGC, NB, NR, IF Shift, IF Width) are already implemented.

The highest-return additions for the next feature sprint would be **BU/BD + UP/DN/TS** (band and step navigation — five commands that together enable full remote tuning without a keyboard), followed by **VA** (anti-VOX, needed by every VOX user operating remote), and **SC** (scan, a common radio function with zero current YWC support).
