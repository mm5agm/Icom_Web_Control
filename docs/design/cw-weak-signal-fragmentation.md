# CW weak-signal fragmentation, and why a threshold control is the wrong fix

**Measured 2026-10-06. All of it offline, from one bench capture — no radio and
no antenna were needed for anything in this document.**

The short version: on a marginal signal the reader prints runs of `E`, `T` and
`I` where the radio's own decoder prints nothing. I assumed that was noise being
transcribed and that a signal threshold would gate it. It is not noise, and a
threshold cannot gate it. This document records the measurements so the wrong
fix does not get re-proposed from the same evidence.

---

## 1. What I saw

Watching IWC's CW reader and the IC-7300 MkII's own built-in decoder side by
side on 14.032 MHz:

| | copy |
|---|---|
| **The radio** | `YO5OED K ? SM7CBS GM 5NN` |
| **IWC** | `CQ YO5OED YO5OED K E TE E E E E EE EE D T T7CBS GM 5NN` |

IWC copied **more** real text — the radio missed `CQ YO5OED YO5OED` entirely —
and also produced runs of single characters the radio did not.

### 1.1 The first correction: some of it was real

I heard the dit-dit in `73 TU EE` with my own ears. **`EE` is dit-dit, the
standard end-of-QSO courtesy.** It is idiomatic CW, the decoder copied it
correctly, and it is the radio that was suppressing real content there.

This matters more than it looks. Hearing two isolated dits is rhythm, not
copy — it needs no Morse literacy, so it is legitimate evidence even though I
do not read CW. And it kills the obvious fix before it is written: anything
that suppresses isolated one-element characters would delete this.

### 1.2 Where the two transcripts actually disagree

One place: the radio read `? SM7CBS`, IWC read `D T T7CBS`.

`M` is `--`, two dahs. We printed `T T`, and `T` is one dah. **That is a single
character split at its element gap.** Likewise `S` = `...` → `E E E`. The
radio's transcript is an *independent* decoder's second opinion on that exact
moment, which makes it the closest thing to outside corroboration this signal
has.

So the defect is **fragmentation of real characters**, not invention of false
ones, and it is narrow.

---

## 2. Reproducing it offline

`cw-real-20261006-yo5oed-14032.wav` (75.5 s, 48 kHz mono, 0 dropped frames,
captured with the reader running and nobody listening) reproduces it exactly
and deterministically through YWC's `tools/CwBench`.

The capture is written to `%APPDATA%\MM5AGM\Icom Web Control\CW Captures\`, and
a copy of it plus its sidecar now sits in YWC's `bench/`, which is where the
harness expects recordings. **`bench/` is gitignored in both repos** — the wavs
are local evidence, not repository content — so on another machine the file has
to be copied across by hand before any of this can be re-run:

```
CwBench.exe cw-real-20261006-yo5oed-14032.wav --pitch 700 --filter 400 --raw
```

```
YO5OEDK CQYO5OED YO5OED K EA7MT EA7MT GM 5NN TBGM5DNXE73 TU EE ?E YO5OED
EEEE E E I W 7NN FERIFES E YO5IMGM5NN FBHPECN/E 73 TUEE
```

Both repos' `core/Services/Cw/*.cs` were byte-identical on the day, so the
harness drives IWC's decoder as faithfully as YWC's.

---

## 3. Four things that do not fix it

Every knob the bench exposes, swept on that recording:

| swept | result |
|---|---|
| `--min-elements` (elements per character) 1.7 → 2.6 | **no change** until 2.6, which also deletes the real `7NN FERIFES`. `EEEE` survives throughout. |
| `--min-element` (minimum mark length) 12 → 36 ms | **no change.** At 26 wpm a dit is 46 ms, so 36 ms is 78% of one. Those `E`s are full-length dits, **not noise spikes.** |
| `--char-gap` (character boundary) 2.0 → 4.0 dits | merges the fragments only by breaking real characters: `EA7MT`→`U7MT`, `YO5OED`→`YOEDK`. |
| `--no-track`, narrowed `--search` | no improvement. `--no-track` was **worse**. |

The minimum-mark result is the decisive one. If the mush were noise, a floor at
78% of a dit would have removed it. It did not, because the marks are real.

---

## 4. Why a threshold control cannot work

The engine already gates text: `CwDecoderEngine.Gate` prints only while the
stream is `Readable` **and** `SignalPresent`, and
`ReadabilityMinElementsPerChar = 1.70` exists specifically to catch `E`/`T`
runs. Neither fires here.

The reason is that **the good and bad populations overlap, and in fact invert.**
At one-second telemetry resolution on this recording:

| | SNR | confidence |
|---|---|---|
| the real `C` of `CQ` | **12.7 dB** | **0.44** |
| the fragmented `EEE` | **15.9 dB** | **0.81** |

Any level or confidence gate set high enough to suppress the fragments deletes
`CQ YO5OED` — the very text we copied and the radio did not. Add §1.1 and it is
worse still: such a gate would also delete the genuine `TU EE`. It would
destroy good copy in order to remove nothing.

**So: no threshold control as a correctness fix.** Recorded here as tried and
rejected, with the numbers, so it does not get re-derived.

### 4.1 The one version worth having

A threshold is defensible as an explicit **operator preference**, and the case
for it is accessibility. Mush read aloud is worse than silence for a partially
sighted operator, who cannot skim past it the way a sighted operator can. A
"print nothing below *x*" control lets that operator choose the radio's
behaviour while a contest operator keeps ours. It must be labelled a
preference, not a fix, and it must warn that turning it up loses weak
callsigns — because that is measured, not speculative.

---

## 5. The mechanism

Below roughly 15 dB SNR the key-up/key-down decision goes genuinely ambiguous.
Marks shorten, gaps stretch, and an intra-character gap crosses the 2-dit
character boundary — so one character is emitted as several. `M` → `T T`,
`S` → `E E E`.

That also explains the whole side-by-side result. The radio suppresses that
region wholesale; we try to copy it. **The two behaviours are the same
trade-off set differently**, which is why we won on the weak opening `CQ` and
lost in the weak middle. It is not that one decoder is better.

---

## 6. The prototype: rejoin, not suppress

`CwElementDecoderOptions.RejoinFragmentMaxGapDits` — **default `0.0`, which is
off, which is the behaviour everything before this change had.**

When on, consecutive one-element characters separated by a gap only marginally
over the character boundary are held, and if joining their symbols yields a
valid Morse character that character is emitted instead (`T T` → `M`,
`E E E` → `S`). A run that cannot be explained is emitted exactly as it
arrived, never dropped. Only one-element characters are ever considered,
because a split inside a character leaves single marks either side of it and a
single mark is only ever `E` or `T`.

### 6.1 Measured

Scored with CwBench's own accuracy metric (Levenshtein over a normalised form)
against the **ARRL W1AW practice set, whose text is published by ARRL** — the
only true external ground truth in the corpus, and the hardest test of the gap
classifier, since W1AW sends every speed below 18 wpm at the same 14.6 wpm
element rate and varies only the gaps.

| file | off | 2.5 | 3.0 | 3.5 |
|---|---|---|---|---|
| 260303_20 | 99.3% | 99.3% | 96.9% | 96.9% |
| 260303_25 | 99.4% | 99.4% | 95.9% | 95.9% |
| 260303_30 | 99.5% | 99.5% | 96.9% | 96.9% |
| 260303_35 | 99.3% | 99.3% | 96.7% | 96.7% |
| 260303_40 | 99.8% | 99.8% | 97.3% | 97.3% |
| 260304_05 | 96.8% | 96.8% | 96.8% | 96.8% |
| 260304_10 | 96.5% | 96.5% | 96.5% | 96.5% |
| 260304_13 | 98.8% | 98.8% | 98.8% | 98.8% |
| 260304_15 | 98.6% | 98.6% | **93.8%** | **93.8%** |
| 260304_18 | 99.8% | 99.8% | 97.1% | 97.1% |
| **mean** | **98.8%** | **98.8%** | 96.7% | 96.7% |

- **The `off` column reproduces `bench/arrl-baseline.txt` exactly**, on all ten
  files. The default path is provably unchanged.
- **2.5 dits costs nothing** — identical on all ten.
- **3.0 dits and above regress**, by 2.1 points of mean and 4.8 on 260304_15.
  So the margin has a ceiling, and it is below 3.0.
- **`TU EE` survives at every margin tested, 3.5 included.** The genuine
  dit-dit is not at risk, which is the §1.1 requirement met.

### 6.2 What this does NOT show

**It is proven safe, not proven useful.** On the real QSO at 2.5 the only change
is `EEEE` → `IEE`, and neither of those is correct.

The honest reason: **the defect I actually reported is not in this recording.**
`T T7CBS` came from watching the panel live at a later moment; the wav covers an
earlier minute. So the one case with independent corroboration — the radio's
`SM` against our `T T` — cannot be scored against this file.

**The next step is therefore a capture, not a code change:** record the moment
where the radio and the reader disagree, with both transcripts noted at the
time. Then this option can be scored on the case it was built for. Until that
exists, the option stays off by default, and nothing here justifies turning it
on.

---

## 7. The test bed, and where it should live

Everything above came from replaying one wav. That makes weak-signal decoder
work repeatable and antenna-free for the first time, and any future change can
be scored before it goes near the radio.

`CwBench` lives in `Yaesu_Web_Control/tools/CwBench` but drives `core/` code,
and `core/`'s rule is that tests for shared code are shared too. **It belongs in
`Radio_Web_Control_Core`.** Moving it needs the `gh pr list` check first,
because a file that moves into `core/` turns its old path into a generated,
gitignored artefact and any open PR touching it then merges as modify/delete.

The ARRL wavs stay local — ARRL holds the copyright, and `arrl-score.sh` prints
scores and never text.

73, Colin MM5AGM
