# CI-V over the [LAN] port

**Status:** proposed. **Phase A gate PASSED 2026-08-13** — measured 30 sweeps/sec
over LAN against 4.1 over USB (§1). Written 2026-08-13.
**Goal:** use the IC-7300 MkII's Ethernet socket instead of the USB lead when it
is available, primarily to lift the band scope off its ~4 sweeps/second ceiling.

---

## 1. Why — the measurement that prompted this

Measured on Colin's IC-7300 MkII, 2026-08-13, scope streaming, IWC stopped and
nothing on the bus but a passive listener on COM8:

| | 19200 | 115200 |
|---|---|---|
| sweep period | 239.5 ms | 239.8 ms |
| first → last segment of one sweep | 212.2 ms | 213.1 ms |
| idle gap between sweeps | 27.2 ms | 26.7 ms |

Two conclusions, both firm:

1. **The USB CDC port ignores the baud setting.** 597 wire bytes in 212 ms is
   ~28 kbps — above 19200 — so the port was never really running at 19200. This
   is why 19200 and 115200 measure identically, down to the segment.
2. **Delivery occupies 89% of the sweep period.** The radio is not thinking
   slowly and IWC is not polling too hard (the listener did no polling at all
   and the period moved by under 4 ms). The radio dribbles its 11 CI-V segments
   out roughly 21 ms apart, and that pacing is the ceiling.

The MkII CI-V reference, command `27 00`:

> **Division number (Maximum): 01 (LAN), 11 (USB)**
> When data is sent to the controller (PC) through the [LAN] port on the
> transceiver's rear panel, it is sent **all at once**. On the other hand, when
> the data is sent through the [USB] port, it is divided into 11 segments and
> sent in sequential order.

One 490-byte block over LAN against eleven paced segments over USB.

### This has now been measured, and it is no longer a projection

wfview was pointed at the radio over LAN and a 15-second tshark capture taken of
UDP port 50002 (`lan.pcapng`, scratchpad). 451 scope frames, every one a single
526-byte UDP packet:

| | USB | LAN |
|---|---|---|
| sweep rate | 4.1 /sec | **30.0 /sec** |
| sweep period | 239.5 ms | **33.3 ms** |
| min / max period | — | 20.0 / 46.2 ms |
| jitter (sd) | — | 3.4 ms |
| frames per sweep | 11, paced ~21 ms | **1** |

**7.3× faster**, and measured while wfview was *also* streaming audio on port
50003 (~1,500 packets/sec both ways). IWC carries no audio, so it is not
competing for that bandwidth. The 20 ms minimum period shows the radio can
exceed 30/sec in bursts.

At 33 ms a sweep, a 60 ms dot at 20 WPM spans two sweeps rather than falling
between them — CW keying becomes visible on the trace, which was the original
complaint behind issue #2. (Still not for Steve; see below.)

**Now proven:** the USB pacing, the 89% figure, the baud irrelevance, the
LAN/USB split in the spec, the network handshake (§2), and the LAN sweep rate.
Nothing in the justification rests on inference any more.

Note this benefits **MkII owners only**. The original IC-7300 has no [LAN]
socket at all (zero references in its full manual) while splitting its sweep 11
ways over USB exactly as the MkII does. Steve on issue #2 — whose reports
started this — cannot benefit, and has been told so.

## 2. What is already confirmed on the bench

Colin's radio, 2026-08-13, `Network Control` switched on and the radio restarted:

- **192.168.68.55**, MAC `00:90:C7:16:EB:51` (OUI `00:90:C7` = Icom Inc.), TTL 255.
- Answers the RS-BA1 "are you there" on UDP **50001** with an "I am here",
  radio ID `0x3787ced2`.
- Ports are the defaults: control **50001**, serial **50002**, audio **50003**.

The 16-byte control packet is therefore **verified working**, not guessed:

```
offset  size  field
0       u32le total length (16)
4       u16le type       (0x03 = are you there, 0x04 = I am here)
6       u16le sequence
8       u32le our id
12      u32le their id   (0 until known)
```

Everything past this point — the ready handshake, the login packet and its
passcode obfuscation, token renewal, the 21-byte ping/data packets, and the
open/close that starts the CI-V stream on 50002 — is **not yet confirmed here**
and must be taken from wfview (see §3) rather than assumed.

## 3. Licence position — better than expected

**IWC is GPLv3 and wfview is GPLv3.** The protocol work can therefore be
*ported*, not reverse-engineered: wfview's `udphandler` / `udpbase` /
`udpserver` code may be adapted directly, provided the GPL terms are honoured
and authorship is attributed in the file headers and in `README.md`.

This is the single biggest cost reduction available and the plan assumes it.
Do **not** start by writing a protocol implementation from first principles.

## 4. Where it plugs in

`Services/Civ/ICivClient.cs` is already the transport seam and is deliberately
protocol-light: callers hand it built frames and get parsed `CivFrame`s back.
Nothing above it knows about serial ports. `CivRadioController` and everything
above it need no changes at all.

**The one wrinkle:** the seam's opener is serial-shaped.

```csharp
Task<bool> OpenAsync(string portName, int baudRate);
```

There are exactly **two call sites** — `CivRadioController.cs:239` and
`CivRadioController.cs:2197` — so widening it is cheap. Replace it with a
transport-neutral descriptor:

```csharp
Task<bool> OpenAsync(CivEndpoint endpoint);   // serial: port+baud; network: host+ports+credentials
```

### New files

```
Services/Civ/Net/
  IcomUdpStream.cs        base: sequence numbers, retransmit, 100 ms keepalive,
                          idle packets, the 16-byte control packets of §2
  IcomControlStream.cs    port 50001 — ready handshake, login, token renewal,
                          conninfo/capabilities
  IcomCivStream.cs        port 50002 — open/close, CI-V bytes in and out
  IcomDiscovery.cs        probe one host; optionally sweep the local /24
Services/Civ/
  CivNetworkBusService.cs ICivClient over the above
Models/
  CivEndpoint.cs          the transport-neutral descriptor
```

`CivNetworkBusService` **reuses `CivFrameBuffer` and `CivFrame` unchanged** — it
receives the same `FE FE … FD` byte stream, just carried in UDP payloads instead
of arriving on a serial port. It should keep `CivBusService`'s echo filter
verbatim even though a point-to-point link should not echo: the filter is cheap
and the failure mode it guards against (issues #2 and #5 — reading our own
address back as the radio's) is severe.

## 5. The one change outside the transport — and it is easy to miss

`CivScopeAssembler` **cannot assemble a LAN sweep as written.**

`Add()` (`Services/Civ/CivScopeAssembler.cs:81`) treats order `01` as a
header-only frame: it calls `StartSweep`, sets `_expectedOrder = 2` and returns
`null`. Over LAN the radio sends `division = 01` with `order = 01`, and that
single frame carries the header **and** all 475 waveform bytes. The current code
would consume it as a header, add no bins, and never complete a sweep — the
panel would sit on "Waiting for the radio's band scope…" forever.

**The layout is confirmed from capture, so this needs no guesswork.** One
526-byte UDP packet, payload 518 bytes:

```
payload[0..20]   Icom 21-byte data-packet header
payload[21..]    fe fe e1 b6 27 00 <15-byte header> <475 waveform bytes> fd
                        ^^ wfview's controller address; IWC would see E0
```

497-byte CI-V frame, 490 data bytes after `27 00` — matching the manual's LAN
figure exactly. Critically, **the 15-byte header is the same layout as the USB
first segment**, so `StartSweep` already parses it correctly.

Fix: in `Add()`, when `div == 1`, call `StartSweep` as now and then consume the
remaining bytes of the *same* frame as waveform and complete the sweep, instead
of returning `null`. No new parsing, no new offsets.

`BuildSweep()` already tolerates a bin count that is not exactly 475, so a
short or long capture degrades rather than corrupts.

## 6. The gate — passed, and passed without writing any code

The plan originally called for a throwaway login-and-count-sweeps harness. That
proved unnecessary: **running wfview and capturing its traffic answers the same
question for free**, with the advantage that the bytes come from this radio and
this firmware rather than from someone else's source.

Done 2026-08-13, results in §1 and §5. **Gate passed: 30/sec against 4.1/sec.**

Keep the technique for the remaining unknowns. Anything still uncertain about
the handshake (§2) can be resolved the same way — reconnect wfview with tshark
running and read the login, token-renewal and open/close exchanges straight off
the wire, rather than inferring them from wfview's source. Ground truth beats
reading C++, and it sidesteps the derivation question in §3 for the parts that
turn out to be obvious.

Capture recipe, for repeating it:

```powershell
& "C:\Program Files\Wireshark\tshark.exe" -i "\Device\NPF_{053860C9-...}" `
    -f "udp and host 192.168.68.55" -a duration:15 -w lan.pcapng
```

Interface is `Ethernet` (PC is 192.168.68.50). Filter `udp.srcport==50002` for
CI-V, `50001` for control, `50003` for audio.

## 7. "If the radio is connected" — the selection rule

### The governing principle: LAN is additive, and off is the default

**A user who never touches the new settings must see no change whatsoever.**
Same serial port, same baud, same behaviour, same failure messages. Every branch
below therefore ends at "carry on exactly as today" unless the network path is
both *configured* and *proven reachable*. LAN is an opt-in improvement for MkII
owners on a wired network, not a new way for the app to fail for everyone else.

This is not just caution about a new feature. The measurement in §1 came from one
radio on one LAN (§9), so the network path will ship less tested than the serial
path no matter how careful the build is.

### The startup decision, in order

Run once, at connect time, before any CI-V is sent:

1. **`ConnectionMode == Serial`** → open the serial port. Done. This is today's
   code path, untouched, and it is what an unconfigured install gets.
2. **No `NetworkHost` configured** → serial. Nothing to probe.
3. **No local IPv4 interface on the same subnet as `NetworkHost`** → serial,
   logged as *"radio LAN address is not on any network this PC can reach"*.
   Catches the laptop taken away from the shack, and costs nothing to check.
4. **Send the §2 "are you there" to `NetworkHost:50001`.** No reply within the
   budget below → serial, logged as *"no answer from the radio on the network"*.
   Catches the radio switched off, Ethernet unplugged, or Network Control turned
   back off — which is easy to do, since it defaults to off and needs a restart
   to take effect.
5. **Reply received** → network transport. Log which one won, at Information.

Steps 3 and 4 together are the actual "is the radio connected to a LAN" test.
A configured host is a statement of intent, not evidence.

**Timing budget: 1 second, once.** A configured-but-unreachable radio must not
delay start-up, and must not retry in a loop before falling back. Users on a
laptop that moves between the shack and elsewhere will hit step 3 or 4 routinely
and it must cost them a second at most.

**`ConnectionMode == Network` is the one mode that does not fall back.** If the
user has explicitly said network-only, silently using the serial port instead
hides a fault they asked to be told about. Fail with the reason from step 3 or 4.

### After start-up

- **Never switch transport mid-session.** If the LAN drops, report it and
  reconnect on the same transport. A silent flip to serial would change the
  scope's segment shape underneath a running assembler — 1 frame per sweep
  versus 11 — and §5 would then be assembling the wrong shape.
- **Say which transport is live** on Diagnostics and in the connection banner.
  A user who cannot tell whether they are on USB or LAN cannot report a bug
  about it, and the whole point of the feature is a difference in sweep rate
  they can see but not attribute.

### The Settings page

A new **Radio connection** section, above the existing serial fields rather than
replacing them, because the serial fields still apply in `Auto` and `Serial`.

```
Radio connection
  Connection      ( ) USB / serial only          <- today's behaviour
                  (o) Use the network if available, otherwise USB   [default]
                  ( ) Network only

  --- shown only when a network option is selected ---
  Radio IP address   [ 192.168.68.55        ]  [ Detect ]
  Control port       [ 50001 ]
  Network user ID    [                      ]
  Network password   [ ********             ]

  [ Test connection ]      Status: not tested
```

Behaviour:

- **The three radio buttons are `ConnectionMode`.** Worded as outcomes rather
  than as `Auto`/`Serial`/`Network`, because "Auto" does not tell a user what
  will happen. The default is the middle one, which for anyone who fills nothing
  in is identical to today.
- **The network fields hide entirely** under "USB / serial only", so the page
  does not grow for users who will never use this.
- **Detect** sweeps the local /24, keeps hosts whose ARP entry carries OUI
  `00:90:C7`, probes each with the are-you-there, and fills the address in. That
  is exactly the sequence that found the radio on the bench and it took under a
  minute. Offer a list if it finds more than one.
- **Test connection** runs the full §2 handshake and reports one of: *radio
  answered*, *no answer* (with the step-3/step-4 reason), *another program is
  using the radio's network connection* (§9), or *wrong user ID or password*.
  This matters more than usual — Network Control defaults to off and needs a
  radio restart, so the most common failure will be a setting on the radio, not
  in IWC, and the message has to say so.
- **Saving does not reconnect.** Say so, and say a restart is needed, rather
  than half-swapping the transport under a live poll loop.
- The radio's own **Network User ID / password** live in its Set → Network menu,
  and the manual section for this must say that, because nobody will guess it.

### Settings to add

| Setting | Default | Notes |
|---|---|---|
| `ConnectionMode` | `Auto` | `Auto` \| `Serial` \| `Network` — default is today's behaviour |
| `NetworkHost` | `""` | empty = never try the network |
| `NetworkControlPort` | `50001` | serial/audio ports are learned from the radio |
| `NetworkUserId` | `""` | radio's Network User ID |
| `NetworkPassword` | `""` | see below |

**Two gotchas, both already documented in `CLAUDE.md`:**

- `Settings.cshtml.cs` needs `ModelState.Remove("Settings.X")` for **every one
  of these**, because `<Nullable>enable</Nullable>` puts an implicit `[Required]`
  on non-nullable strings and silently blocks saving an empty value — and empty
  is the "feature off" value for all of them.
- `SettingsService` is read-modify-write on `appsettings.user.json` with no lock
  across the sequence. Pre-existing, not introduced here, but adding five
  settings widens the window.

**Credentials at rest.** `appsettings.user.json` is plaintext today. A radio
password is a different class of secret from a COM port number, and the file
sits in `%APPDATA%` where any process running as the user can read it. Protect
`NetworkPassword` with DPAPI (`ProtectedData.Protect`, `CurrentUser` scope)
rather than storing it as typed, and say so in the manual. This is a decision to
take before shipping, not after.

## 8. Explicitly out of scope

- **Audio over LAN (port 50003).** IWC carries no receiver audio at all today
  and this plan does not change that. It is the obvious follow-on and it is a
  much larger job — codecs, buffering, latency — so it gets its own plan or none.
- **Remote operation over the internet.** Everything here assumes the radio is
  on the local network. Port-forwarding CI-V to the internet is a security
  discussion IWC should not start by accident.
- **The original IC-7300.** No LAN socket; nothing to do.

## 9. Risks

- **One controller at a time.** If RS-BA1 or wfview holds the network session,
  IWC will be refused. The failure must be reported as "another program is
  controlling the radio over the network", not as a generic connect failure.
- **Power off.** Over USB, `18` (power) is excluded from the trusted voice-macro
  set because powering the radio off drops the serial port with it. Over LAN the
  radio can be left in a standby state that still answers the network — so the
  reasoning behind that exclusion changes, and should be re-examined rather than
  copied across.
- **Untestable by anyone but Colin.** Only one MkII on one LAN is available. Any
  release carrying this needs the transport defaulting to serial-unless-
  configured, so a bug in it cannot break users who never asked for it.
- **The spike may kill the project.** That is the point of §6.

## 10. Sequence

| Phase | Work | Gate |
|---|---|---|
| ~~**A**~~ | ~~§6 spike~~ — **done 2026-08-13 via wfview + tshark** | **passed: 30/sec vs 4.1/sec** |
| **B** | `CivEndpoint`, widen `ICivClient.OpenAsync`, `CivNetworkBusService` + the three `Net/` classes | connects, polls, meters live |
| **C** | §5 assembler fix using the captured offsets | scope draws over LAN |
| **D** | §7 in full: Settings section, Detect, Test connection, the five-step startup decision, Diagnostics transport readout | **an install that configures nothing behaves identically to today**, and a configured one falls back cleanly with the radio's Ethernet unplugged, the radio switched off, and Network Control turned back off |
| **E** | `USER_MANUAL.md` §6.1, README release notes, wfview attribution | release rules 13/14 |

Related: `iwc-clone-split-plan.md` (this is not one of its phases — it postdates
the plan), and the `iwc-lan-transport-opportunity` memory note for the raw
measurements.
