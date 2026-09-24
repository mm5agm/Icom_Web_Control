// RTTY crossed-ellipse tuning scope.
//
// How RTTY was tuned before anything had a waterfall: the terminal unit's mark
// filter went to the X plates of an oscilloscope and its space filter to the Y
// plates. Each tone then draws a line - mark flat across, space straight up -
// and a signal shifting between them draws a cross:
//
//   on tune          a clean cross, each arm a thin ellipse
//   off tune         the arms lean towards each other and open up, because
//                    each tone now leaks into both filters
//   wrong shift      one arm right, the other a blob
//   nothing there    a fuzzy ball of noise in the middle
//
// Tune for the cross. The filtering is done on the host (RttyTuningScope); this
// file only draws the sweeps that arrive and keeps the last few on screen,
// fading, the way the phosphor did.
//
// Radio-agnostic: it talks to /api/rtty and nothing else. The host page
// provides the dialog and the elements named in the constructor defaults.

import { RTTY_SHIFTS as SHIFTS, DEFAULT_RTTY_SETTINGS, loadRttySettings, saveRttySettings } from './rtty-settings.js';

const POLL_MS    = 50;     // 20 redraws a second
const POINTS     = 500;    // about 21 ms at 24,000 points a second: one sweep
const PERSIST    = 6;      // sweeps kept on screen, oldest dimmest
const QUIET_DB   = -80;    // below this in both filters there is nothing to draw
const CHUNK      = 5;      // points per stroke: half a cycle of 2 kHz at 24,000 points a second
const RADIO_MS   = 4000;  // how often to re-read the radio's RTTY menu while open

// How much of what the receiver passes lands in the two tone filters, in dB.
// RTTY on tune puts nearly all of it there; noise, or a signal off to one side,
// puts most of it elsewhere.
function filterShare(f) {
    const inFilters = 10 * Math.log10(Math.pow(10, f.markDb / 10) + Math.pow(10, f.spaceDb / 10));
    return inFilters - f.inputDb;
}

export class RttyTuner {
    constructor(ids = {}) {
        this._ids = Object.assign({
            dialog:  'rttyTunerDialog',
            canvas:  'rttyTunerCanvas',
            info:    'rttyTunerInfo',
            status:  'rttyTunerStatus',
            mark:      'rttyTunerMark',
            shift:     'rttyTunerShift',
            reverse:   'rttyTunerReverse',
            fromRadio: 'rttyTunerFromRadio',
        }, ids);

        this._timer    = null;
        this._inFlight = false;
        this._sweeps   = [];       // arrays of [x, y, ...] in amplitude units
        this._scale    = 1e-4;     // decaying peak, so the figure fills the face
        this._last     = null;
        this._settings = { ...DEFAULT_RTTY_SETTINGS };
        this._radioProbed = false;
        this._radioTimer   = null;
        this._radioApplied = null;   // {markHz, shiftHz} we last took FROM the radio
        this._want    = null;        // settings we have POSTed but not yet seen come back
        this._wantAt  = 0;
    }

    init() {
        const $ = id => document.getElementById(this._ids[id]);
        this._dialog  = $('dialog');
        this._canvas  = $('canvas');
        this._info    = $('info');
        this._status  = $('status');
        this._markEl  = $('mark');
        this._shiftEl = $('shift');
        this._revEl   = $('reverse');
        this._radioEl = $('fromRadio');
        if (!this._dialog || !this._canvas) return false;

        this._ctx = this._canvas.getContext('2d');
        this._loadSettings();
        this._showSettings();

        const changed = () => { this._readSettings(); this._saveSettings(); this._send('start'); };
        this._markEl?.addEventListener('change', changed);
        this._shiftEl?.addEventListener('change', changed);
        this._revEl?.addEventListener('change', changed);

        // The audio is held only while the dialog is open. Closing it by any
        // route - the X, Escape, or the page's own code - lets the host go.
        // The radio sync goes with it: it costs CI-V traffic and there is
        // nothing to keep in step with once the figure is gone.
        this._dialog.addEventListener('close', () => {
            this._stopPolling();
            this._stopRadioSync();
            this._send('stop');
        });

        if (window.ResizeObserver) {
            new ResizeObserver(() => { this._resize(); this._draw(); }).observe(this._canvas);
        }
        this._resize();
        this._draw();
        return true;
    }

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) { this._dialog.close(); return; }
        // Non-modal: the operator tunes the VFO while watching the figure.
        this._dialog.show();
        this._resize();
        if (this._radioEl && !this._radioProbed) {
            this._radioProbed = true;
            this._radioEl.addEventListener('click', () => this._syncFromRadio(true));
        }
        this._send('start');
        this._startPolling();
        this._startRadioSync();
    }

    // ── Settings ────────────────────────────────────────────────────────────

    // Shared with click-to-tune, which reads them for AFSK modes.
    _loadSettings() {
        this._settings = loadRttySettings();

        // Storage does not record whether the saved tones were typed or taken
        // from the radio, and on a reload that is exactly what the sync needs
        // to know. Assume the keyboard unless they are still the factory pair:
        // guessing the other way throws a typed setting away once per reload,
        // and not throwing it away is the whole point of _radioApplied. The
        // cost of guessing this way is one press of From radio.
        const untouched = this._settings.markHz  === DEFAULT_RTTY_SETTINGS.markHz
                       && this._settings.shiftHz === DEFAULT_RTTY_SETTINGS.shiftHz;
        this._radioApplied = untouched ? { ...this._settings } : null;
    }

    _saveSettings() { saveRttySettings(this._settings); }

    _showSettings() {
        if (this._markEl)  this._markEl.value    = String(this._settings.markHz);
        if (this._shiftEl) this._shiftEl.value   = String(this._settings.shiftHz);
        if (this._revEl)   this._revEl.checked   = this._settings.reverse;
    }

    _readSettings() {
        const mark  = Number(this._markEl?.value);
        const shift = Number(this._shiftEl?.value);
        if (Number.isFinite(mark) && mark >= 300 && mark <= 3000) this._settings.markHz = Math.round(mark);
        if (SHIFTS.includes(shift)) this._settings.shiftHz = shift;
        this._settings.reverse = !!this._revEl?.checked;
    }

    // ── Host ────────────────────────────────────────────────────────────────

    async _send(what) {
        // Remember what we are asking for BEFORE the request goes out. Frames
        // arriving in the meantime still describe the old filters, and
        // _adoptServerSettings must not mistake one of them for somebody else
        // overruling us - see there.
        if (what === 'start') {
            this._want   = { markHz: this._settings.markHz, shiftHz: this._settings.shiftHz, reverse: !!this._settings.reverse };
            this._wantAt = Date.now();
        }
        try {
            const res = await fetch(`/api/rtty/tuner/${what}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: what === 'start' ? JSON.stringify(this._settings) : '{}',
            });
            if (!res.ok) {
                const body = await res.json().catch(() => ({}));
                this._setStatus(body.error || `HTTP ${res.status}`);
                return;
            }
            if (what === 'start') this._sweeps.length = 0;
        } catch {
            this._setStatus('Cannot reach the server.');
        }
    }

    // ── Mark and shift from the radio ───────────────────────────────────────
    //
    // Optional in both directions. The host page need not provide the button,
    // and an app whose backend has no /api/rtty/radio-tones stops asking after
    // the first 404 - so this is safe to ship before every app implements the
    // endpoint.
    //
    // The tuner follows the radio while the dialog is open, re-reading every
    // few seconds, because the alternative was worse: change the radio's RTTY
    // Shift Width, look at the tuner, and it sits there on the old number
    // looking broken. There is no CI-V event for a SET-menu change, so polling
    // is the only way to notice. Two reads every four seconds, only while the
    // dialog is open.
    //
    // But it only ever overwrites values it put there itself. That is what
    // _radioApplied is for. The radio's menu describes its own FSK decoder and
    // is often simply wrong for what you are listening to - a utility station
    // on 450 Hz shift while the menu says 170 is the ordinary case, not an
    // exotic one - so the moment the operator types something different, the
    // sync backs off and leaves it alone until they ask again. Follow, but
    // never argue.

    _startRadioSync() {
        if (!this._radioEl || this._radioTimer) return;
        this._syncFromRadio(false);
        this._radioTimer = setInterval(() => this._syncFromRadio(false), RADIO_MS);
    }

    _stopRadioSync() {
        if (this._radioTimer) { clearInterval(this._radioTimer); this._radioTimer = null; }
    }

    // manual: the operator pressed the button, so say what happened either way
    // and apply even in an AFSK mode, where they may well have a reason.
    async _syncFromRadio(manual) {
        try {
            const res = await fetch('/api/rtty/radio-tones');
            if (res.status === 404) {          // this app cannot answer: stop asking
                if (this._radioEl) this._radioEl.hidden = true;
                this._stopRadioSync();
                return;
            }
            if (!res.ok) { if (manual) this._setStatus(`Radio settings: HTTP ${res.status}`); return; }
            const r = await res.json();
            if (!r.ok) { if (manual) this._setStatus(r.reason || 'The radio did not answer.'); return; }

            const mark  = Number.isFinite(r.markHz) ? Math.round(r.markHz) : null;
            const shift = SHIFTS.includes(r.shiftHz) ? r.shiftHz : null;
            if (mark === null && shift === null) return;

            if (!manual) {
                // Not while they are typing a mark, and not in an AFSK mode,
                // where the tones belong to their software and this menu is
                // describing something else entirely.
                if (!r.fsk) return;
                if (document.activeElement === this._markEl) return;
                // Only replace what we ourselves last took from the radio.
                // Anything else on screen is the operator's, including on a
                // fresh page where we do not know - see _loadSettings.
                const a = this._radioApplied;
                if (!a || a.markHz !== this._settings.markHz || a.shiftHz !== this._settings.shiftHz) return;
            }

            const before = { ...this._settings };
            if (mark  !== null) this._settings.markHz  = mark;
            if (shift !== null) this._settings.shiftHz = shift;
            this._radioApplied = { markHz: this._settings.markHz, shiftHz: this._settings.shiftHz };

            const same = before.markHz === this._settings.markHz
                      && before.shiftHz === this._settings.shiftHz;
            if (same && !manual) return;       // nothing to do, and nothing to say

            this._showSettings();
            this._saveSettings();
            this._send('start');

            const read = `Radio: mark ${this._settings.markHz} Hz, shift ${this._settings.shiftHz} Hz`;
            this._setStatus(r.note ? `${read}. ${r.note}`
                                   : same ? `${read} - already matching.` : `${read}.`);
        } catch {
            if (manual) this._setStatus('Cannot reach the server.');
        }
    }

    _startPolling() {
        if (this._timer) return;
        this._timer = setInterval(() => this._poll(), POLL_MS);
    }

    _stopPolling() {
        if (this._timer) { clearInterval(this._timer); this._timer = null; }
        this._sweeps.length = 0;
        this._last = null;
        this._draw();
    }

    async _poll() {
        // A slow reply must not stack requests behind it: skip a frame instead.
        if (this._inFlight) return;
        this._inFlight = true;
        try {
            const res = await fetch(`/api/rtty/tuner?points=${POINTS}`);
            if (!res.ok) return;
            const f = await res.json();
            this._last = f;
            this._adoptServerSettings(f);
            this._push(f);
            this._draw();
        } catch {
            // One dropped frame in twenty. Not worth saying.
        } finally {
            this._inFlight = false;
        }
    }

    // There is one tuner on the server, so another tab or browser changing
    // its settings changes them for this one too. Show what it is really
    // using, or the dialog says 170 while the filters sit at 450. Not while
    // the operator is typing a mark, and not saved: this page's own choice
    // is still what it starts with next time.
    //
    // This runs twenty times a second, which is what makes the gate below
    // load-bearing rather than tidy. Changing Shift in the dialog POSTs a
    // start and returns; several frames then arrive before the host has
    // re-toned, every one of them still saying 170. Without the gate the
    // first of them put 170 straight back into the dropdown - so the
    // operator's choice visibly sprang back and the change "did not work" -
    // and the pair then chased each other, saving to localStorage
    // synchronously on every frame until the page stopped painting.
    _adoptServerSettings(f) {
        if (!f.running) return;
        const s = this._settings;
        if (document.activeElement === this._markEl) return;

        // Our own start is still in flight. Ignore frames until one comes back
        // carrying what we asked for - or until it is plain the host is not
        // going to (it rejects a mark+shift that will not fit the audio), so a
        // refused setting cannot wedge this shut.
        if (this._want) {
            const w = this._want;
            const arrived = Math.round(f.markHz) === w.markHz
                         && f.shiftHz === w.shiftHz
                         && !!f.reverse === w.reverse;
            if (!arrived && Date.now() - this._wantAt < 3000) return;
            this._want = null;
        }

        let changed = false;
        if (SHIFTS.includes(f.shiftHz) && f.shiftHz !== s.shiftHz) { s.shiftHz = f.shiftHz; changed = true; }
        if (typeof f.reverse === 'boolean' && f.reverse !== s.reverse) { s.reverse = f.reverse; changed = true; }
        if (Number.isFinite(f.markHz) && Math.round(f.markHz) !== s.markHz) { s.markHz = Math.round(f.markHz); changed = true; }

        // Whatever arrived here is another tab's doing, not ours and not the
        // radio's, so the radio sync backs off from it exactly as it does from
        // a typed value. Deliberately not saved, per the note above.
        if (changed) { this._showSettings(); this._radioApplied = null; }
    }

    _push(f) {
        const pts = f.points || [];
        const peak = f.peak || 0;
        if (!pts.length || peak <= 0) return;

        const k = peak / 1000;
        const sweep = new Float32Array(pts.length);
        for (let i = 0; i < pts.length; i++) sweep[i] = pts[i] * k;
        this._sweeps.push(sweep);
        if (this._sweeps.length > PERSIST) this._sweeps.shift();

        // The face follows the loudest recent sweep and relaxes slowly, so a
        // fade shrinks the figure for a moment rather than the figure jumping
        // to fill the face on every sweep - which would make noise look as
        // confident as a signal.
        this._scale = Math.max(peak, this._scale * 0.97, 1e-6);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    _resize() {
        const c = this._canvas;
        if (!c) return;
        const dpr = window.devicePixelRatio || 1;
        const css = Math.max(160, Math.min(c.clientWidth || 280, 480));
        if (this._css === css && c.width === Math.round(css * dpr)) return;
        c.width = c.height = Math.round(css * dpr);
        c.style.height = `${css}px`;
        this._ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
        this._css = css;
    }

    _draw() {
        const ctx = this._ctx;
        if (!ctx || !this._css) return;
        const s = this._css, mid = s / 2, rad = s * 0.44;

        ctx.clearRect(0, 0, s, s);
        ctx.fillStyle = '#0b0f0b';
        ctx.fillRect(0, 0, s, s);

        // Graticule, square this time: the old scopes had a ruled grid.
        ctx.strokeStyle = '#1f3a24';
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let g = -4; g <= 4; g++) {
            const d = rad * g / 4;
            ctx.moveTo(mid - rad, mid + d); ctx.lineTo(mid + rad, mid + d);
            ctx.moveTo(mid + d, mid - rad); ctx.lineTo(mid + d, mid + rad);
        }
        ctx.stroke();
        ctx.strokeStyle = '#2f5a36';
        ctx.beginPath();
        ctx.moveTo(mid - rad, mid); ctx.lineTo(mid + rad, mid);
        ctx.moveTo(mid, mid - rad); ctx.lineTo(mid, mid + rad);
        ctx.stroke();

        ctx.fillStyle = '#3f7a48';
        ctx.font = '10px ui-monospace, Consolas, monospace';
        ctx.textAlign = 'right';
        ctx.fillText('MARK', mid + rad, mid - 4);
        ctx.textAlign = 'left';
        ctx.fillText('SPACE', mid + 4, mid - rad + 10);

        // The face scales to whatever is there, so receiver noise alone
        // fills it as fully as a signal would. Dim it instead, so a ball of
        // noise never looks like something worth tuning.
        const f = this._last;
        const quiet = !f || Math.max(f.markDb ?? -120, f.spaceDb ?? -120) < QUIET_DB
                         || filterShare(f) < -10;
        const k = rad / (this._scale || 1e-6);
        const n = this._sweeps.length;

        // Drawn the way a beam lights phosphor: each step between points adds
        // a little light, so where the trace dwells - along the arms, retraced
        // twice a cycle - it glows, and the quick swings between mark and
        // space stay faint. Drawn at full brightness instead, those swings
        // look as solid as the arms and bury the cross in a tangle.
        ctx.save();
        ctx.globalCompositeOperation = 'lighter';
        ctx.lineWidth = 1.4;
        ctx.lineJoin = 'round';
        for (let j = 0; j < n; j++) {
            const sw = this._sweeps[j];
            const age = (j + 1) / n;                     // newest = 1
            const a = (quiet ? 0.3 : 1) * (0.02 + 0.13 * age * age);
            ctx.strokeStyle = `rgba(60, 255, 100, ${a.toFixed(3)})`;
            // One path is lit once however often it crosses itself, so a
            // sweep is stroked in pieces of about half a tone cycle: short
            // enough not to overlap themselves, so the retraces add up.
            for (let i = 0; i + 2 < sw.length; i += CHUNK * 2) {
                ctx.beginPath();
                ctx.moveTo(mid + sw[i] * k, mid - sw[i + 1] * k);
                const end = Math.min(sw.length, i + CHUNK * 2 + 2);
                for (let p = i + 2; p < end; p += 2) ctx.lineTo(mid + sw[p] * k, mid - sw[p + 1] * k);
                ctx.stroke();
            }
        }
        ctx.restore();

        if (this._info) this._info.textContent = this._caption();
        if (f) this._setStatus(this._statusText(f));
    }

    _caption() {
        const f = this._last;
        if (!f) return 'Waiting.';
        const hz = v => Math.round(v);
        const db = v => (v <= -119 ? '---' : v.toFixed(0));
        return `Mark ${hz(f.markHz)}  Space ${hz(f.spaceHz)} Hz   ` +
               `M ${db(f.markDb)}  S ${db(f.spaceDb)}  in ${db(f.inputDb)} dBFS`;
    }

    _statusText(f) {
        if (!f.running) return 'Stopped.';
        if (f.captureError) return f.captureError;
        if (Math.max(f.markDb, f.spaceDb) < QUIET_DB) return 'No signal - nothing in either filter.';

        const share = filterShare(f);
        const tilt = f.markDb - f.spaceDb;
        const mode = f.mode ? ` (${f.mode})` : '';

        if (share < -10) return `Little of the audio is in the tone filters - tune for the cross${mode}.`;
        if (tilt > 10)  return `Mark only - idling, or the shift or reverse is wrong${mode}.`;
        if (tilt < -10) return `Space only - the shift or reverse is wrong${mode}.`;
        return `Both tones in their filters - fine-tune for the thinnest cross${mode}.`;
    }

    _setStatus(text) {
        if (this._status && this._status.textContent !== text) this._status.textContent = text;
    }
}
