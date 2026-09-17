// CW Send: type a line, press Enter, the radio keys it.
//
// The IC-7300 has a real "send this text" command - CI-V 17 takes up to 30
// characters and keys them from the internal keyer, and 17 FF stops a
// message part way through. So this panel is the simple half of the pair:
// cut the line into pieces of up to 30 characters at word boundaries, send
// each through POST /api/cat/cw/send, and wait for it to finish before the
// next goes. Stop is a real stop, not a promise about the next piece.
//
// This module is IWC-local rather than in core/ on purpose. The piece size
// (30), the character set (Icom's, with ^ as the prosign join) and the very
// existence of a stop are the radio's, not the panel's; the Yaesu app's
// panel of the same name works by borrowing a keyer memory and cannot stop
// at all. What the two share - the keying-time arithmetic and the timeline
// the highlight runs on - is core/js/cw/morse-timing.js.
//
// Bench questions (none of this has seen an antenna; see USER_MANUAL §19):
//   - does the radio ACK a second 17 while the first is still keying, and
//     if so does it queue it or drop it? The wait between pieces is sized
//     so the question does not normally arise.
//   - with break-in OFF, does 17 play to the sidetone only, as the memory
//     keyer does from the front panel? The banner assumes so.
import { durationMs, charTimeline } from '../cw/morse-timing.js';

// CI-V 17 takes at most 30 characters in one command.
const MAX_CHUNK = 30;
// How often the highlight moves along the piece on air.
const CURSOR_MS = 40;
// Slack on top of the computed keying time before the next piece goes:
// the radio's latency between the command and the first element, and a
// keyer whose weighting is not exactly the textbook's. Every millisecond
// here is silence between pieces, so it is small on purpose.
const TIMING_MARGIN_MS = 250;
const TIMING_MARGIN_FRAC = 0.03;

// The same character set the server keeps (CivRadioController.IsCwSendable):
// the chunk lengths have to agree with what the radio will actually key, or
// a 30-char chunk full of dropped punctuation would be padded out by the
// next word. Lower case is sent as upper. ^ is Icom's prosign join, so
// ^AR keys AR with no gap between the letters.
export function cleanCw(text) {
    return (text || '').toUpperCase().replace(/\s+/g, ' ')
        .replace(/[^A-Z0-9 /?.\-,:'()=+"@^]/g, '').replace(/ +/g, ' ').trim();
}

// Split cleaned text into <=30-char pieces at word boundaries. A single
// word longer than 30 characters (nobody sends one, but a pasted string
// might) is cut hard rather than dropped.
export function chunkCw(clean, max = MAX_CHUNK) {
    const out = [];
    let cur = '';
    for (const word of clean.split(' ')) {
        if (!word) continue;
        if (word.length > max) {
            if (cur) { out.push(cur); cur = ''; }
            for (let i = 0; i < word.length; i += max) out.push(word.slice(i, i + max));
            continue;
        }
        if (!cur) cur = word;
        else if (cur.length + 1 + word.length <= max) cur += ' ' + word;
        else { out.push(cur); cur = word; }
    }
    if (cur) out.push(cur);
    return out;
}

export class CwSendPanel {
    constructor(ids = {}) {
        this._ids = Object.assign({
            dialog: 'cwSendDialog',
            input: 'cwSendInput',
            log: 'cwSendLog',
            status: 'cwSendStatus',
            banner: 'cwSendBanner',
            stopBtn: 'cwSendStopBtn',
            clearBtn: 'cwSendClearBtn',
            clearInputBtn: 'cwSendClearInputBtn',
            speedSlider: 'cwSendSpeedSlider',
            speedValue: 'cwSendSpeedValue',
        }, ids);
        this._queue = [];          // [{ no, line, chunks, pos, stopped, el }]
        this._current = null;      // the item whose chunks are going out
        this._running = false;
        this._breakIn = null;      // '0' | '1' | '2' | null unknown
        this._lineNo = 0;
        this._cursor = null;       // interval moving the highlight along the piece on air
        this._wait = null;         // { resolve } for the wait between pieces, so Stop can cut it short
    }

    init() {
        const $ = id => document.getElementById(id);
        this._dialog = $(this._ids.dialog);
        this._input = $(this._ids.input);
        this._log = $(this._ids.log);
        this._status = $(this._ids.status);
        this._banner = $(this._ids.banner);
        this._speedSlider = $(this._ids.speedSlider);
        this._speedValue = $(this._ids.speedValue);
        if (!this._dialog || !this._input) return;

        this._input.addEventListener('keydown', e => this._onKeydown(e));
        $(this._ids.stopBtn)?.addEventListener('click', () => this.stop());
        $(this._ids.clearBtn)?.addEventListener('click', () => this.clearLog());
        $(this._ids.clearInputBtn)?.addEventListener('click', () => this.clearInput());
        if (this._speedSlider) {
            this._speedSlider.addEventListener('input', () => {
                if (this._speedValue) this._speedValue.textContent = this._speedSlider.value;
            });
            this._speedSlider.addEventListener('change', () => this._postSpeed(this._speedSlider.value));
        }
        // The keyer dialog's own controls are the source of truth for what
        // the radio said last; copy them so the box is right before the
        // radio has been asked.
        const ks = document.getElementById('cwSpeedSlider');
        if (ks) this.setSpeed(ks.value);
        const bi = document.getElementById('cwBreakInSelect');
        if (bi) this.setBreakIn(bi.value);
        this._say('Type a line and press Enter to send it. Escape or Stop halts the keying.');
    }

    // ── Open / close ─────────────────────────────────────────────────────

    toggle() {
        if (!this._dialog) return;
        if (this._dialog.open) this._dialog.close();
        else this.show();
    }

    show() {
        if (!this._dialog) return;
        // show(), not showModal(): the operator works the rest of the station
        // while a line is on its way, and reads the CW reader beside it.
        if (!this._dialog.open) this._dialog.show();
        this._input.focus();
        this._refreshFromRadio();
    }

    // ── Radio state ──────────────────────────────────────────────────────

    // The speed and break-in are read from the radio when the panel opens,
    // the same GET the CW keyer dialog uses. While the radio is in CW the
    // poll loop pushes both over SignalR as they change (site.js routes
    // CwSpeed / CwBreakIn here), so this is only the first fill - and the
    // one that counts outside CW mode, where the loop leaves the bus alone.
    async _refreshFromRadio() {
        try {
            const r = await fetch('/api/cat/cw/state');
            if (!r.ok) return;
            const s = await r.json();
            if (typeof s.speedWpm === 'number') this.setSpeed(s.speedWpm);
            if (typeof s.breakIn === 'number') this.setBreakIn(s.breakIn);
        } catch { /* the keyer dialog's values stand */ }
    }

    // Both sliders show the radio's one setting, so both follow whatever
    // was read or written last.
    setSpeed(v) {
        const n = parseInt(v, 10);
        if (!Number.isFinite(n)) return;
        if (this._speedSlider) this._speedSlider.value = n;
        if (this._speedValue) this._speedValue.textContent = n;
        const ks = document.getElementById('cwSpeedSlider');
        const kv = document.getElementById('cwSpeedValue');
        if (ks) ks.value = n;
        if (kv) kv.textContent = n;
    }

    setBreakIn(v) {
        this._breakIn = v == null ? null : String(v);
        const bi = document.getElementById('cwBreakInSelect');
        if (bi && this._breakIn != null && bi.value !== this._breakIn) bi.value = this._breakIn;
        this._renderBanner();
    }

    _renderBanner() {
        if (!this._banner) return;
        // Break-in off is a legitimate practice mode (the radio's own way
        // to audition a memory), so it is a notice, not a refusal.
        const off = this._breakIn === '0';
        this._banner.hidden = !off;
        if (off) this._banner.textContent = 'Break-in is off - text plays to the sidetone only, nothing is transmitted.';
    }

    // ── Input ────────────────────────────────────────────────────────────

    _onKeydown(e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            this.send(this._input.value);
            this._input.value = '';
        } else if (e.key === 'Escape') {
            e.preventDefault();
            // Escape is Stop while anything is going out; with nothing to
            // stop it empties the box instead (a paste that was never
            // meant for the keyer is the usual reason to want that).
            if (this._running || this._queue.length) this.stop();
            else this.clearInput();
        }
    }

    clearInput() {
        if (!this._input) return;
        this._input.value = '';
        this._input.focus();
    }

    // Queue a line. Returns the number of chunks it became, 0 if nothing
    // sendable was in it.
    send(text) {
        const clean = cleanCw(text);
        if (!clean) {
            if ((text || '').trim()) this._say('Nothing sendable in that line - the keyer takes A-Z, 0-9, space and / ? . - , : \' ( ) = + " @ ^ only.', true);
            return 0;
        }
        const chunks = chunkCw(clean);
        const item = { no: ++this._lineNo, line: clean, chunks, pos: 0, stopped: false, el: this._logLine(clean, 'queued') };
        this._queue.push(item);
        this._pump();
        return chunks.length;
    }

    // A real stop: 17 FF halts the piece on air, and nothing after it
    // starts. Queued lines are tagged, the piece's wait is cut short so
    // the queue drains at once.
    async stop() {
        const dropped = this._queue.splice(0, this._queue.length);
        for (const it of dropped) this._tag(it.el, 'dropped', 'not sent');
        const cur = this._current;
        if (cur) {
            cur.stopped = true;
            this._cursorStop(false);
            this._wait?.resolve();
            try {
                const r = await fetch('/api/cat/cw/stop', { method: 'POST' });
                this._say(r.ok ? 'Stopped.' : `Stop was not accepted (HTTP ${r.status}).`, !r.ok);
            } catch (e) {
                this._say(`Stop failed: ${e.message}`, true);
            }
        } else if (dropped.length) {
            this._say('Queue cleared.');
        }
    }

    clearLog() {
        if (this._log) this._log.textContent = '';
    }

    // ── Sequencing ───────────────────────────────────────────────────────

    async _pump() {
        if (this._running) return;
        this._running = true;
        this._setMemButtons(true);
        try {
            while (this._queue.length) {
                this._current = this._queue.shift();
                await this._sendItem(this._current);
                this._current = null;
            }
        } finally {
            this._current = null;
            this._running = false;
            this._setMemButtons(false);
            this._updateQueueStatus();
        }
    }

    async _sendItem(item) {
        const n = item.chunks.length;
        for (let i = 0; i < n; i++) {
            if (item.stopped) {
                this._tag(item.el, 'dropped', i ? `stopped after part ${i} of ${n}` : 'not sent');
                return;
            }
            this._tag(item.el, 'sending', n > 1 ? `sending part ${i + 1} of ${n}` : 'sending');
            this._updateQueueStatus();
            const sent = await this._sendChunk(item.chunks[i]);
            if (!sent) {
                this._tag(item.el, 'error', i ? `failed at part ${i + 1} of ${n}` : 'failed');
                return;
            }
            // Stop pressed while the command was on its way: the 17 FF
            // queued behind it on the bus, so the radio has already halted
            // and there is nothing to follow.
            if (item.stopped) {
                this._tag(item.el, 'dropped', n > 1 ? `stopped in part ${i + 1} of ${n}` : 'stopped');
                return;
            }
            const wpm = this._wpm();
            this._cursorStart(item, sent, wpm);
            await this._follow(sent, wpm);
            this._cursorStop(!item.stopped);
            if (item.stopped) {
                this._tag(item.el, 'dropped', n > 1 ? `stopped in part ${i + 1} of ${n}` : 'stopped');
                return;
            }
        }
        const onAir = this._breakIn !== '0';
        this._tag(item.el, onAir ? 'sent' : 'monitor', onAir ? 'sent' : 'sidetone only');
    }

    // One CI-V 17 through the server. Returns the text the radio was given,
    // or null if the piece could not be sent (the line is abandoned then -
    // text arriving with a hole in it is worse than text that stops).
    async _sendChunk(chunk) {
        let r, d;
        try {
            r = await fetch('/api/cat/cw/send', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: chunk }),
            });
            d = await r.json().catch(() => ({}));
        } catch (e) {
            this._say(`Send failed: ${e.message}`, true);
            return null;
        }
        if (r.ok) return d.sent || chunk;
        this._say(d.error || `Send failed (HTTP ${r.status}).`, true);
        return null;
    }

    // The speed the radio is keying at, as far as the page knows: the
    // slider, which follows the radio's own answer whenever the panel or
    // the keyer dialog opens.
    _wpm() {
        let wpm = parseInt(this._speedSlider?.value, 10);
        if (!(wpm >= 6 && wpm <= 48)) wpm = parseInt(document.getElementById('cwSpeedSlider')?.value, 10);
        if (!(wpm >= 6 && wpm <= 48)) wpm = 20;
        return wpm;
    }

    // Wait for the piece to finish before the next one is sent: the
    // computed keying time plus a small margin. The radio reports nothing
    // about where it is in the text, so this is the textbook time at the
    // speed the slider shows. Stop resolves it early.
    _follow(sent, wpm) {
        const keying = Math.max(300, durationMs(sent, wpm));
        const margin = TIMING_MARGIN_MS + keying * TIMING_MARGIN_FRAC;
        return new Promise(resolve => {
            const id = setTimeout(() => { this._wait = null; resolve(); }, keying + margin);
            this._wait = { resolve: () => { clearTimeout(id); this._wait = null; resolve(); } };
        });
    }

    // ── The character under the key ──────────────────────────────────────
    //
    // Nothing comes back from the radio about where it is in the text, so
    // this is the textbook timeline run from the moment the command went:
    // the same clock the wait between pieces uses. Off by the radio's own
    // start latency, and by whatever its keyer does that the textbook does
    // not - a display, not a measurement.

    _cursorStart(item, chunk, wpm) {
        this._cursorStop(true);
        const el = item.el;
        if (!el || !chunk) return;
        const from = item.line.indexOf(chunk, item.pos);
        if (from < 0) return;
        item.pos = from + chunk.length;
        const spans = el.querySelectorAll('.cws-ch');
        const timeline = charTimeline(chunk, wpm);
        const t0 = Date.now();
        let shown = null;   // the span last scrolled into view
        const tick = () => {
            const t = Date.now() - t0;
            let cur = null;
            for (const e of timeline) {
                const sp = spans[from + e.index];
                if (!sp) continue;
                const isCur = e.start <= t && t < e.end;
                sp.classList.toggle('cws-done', e.end <= t && e.end > e.start);
                sp.classList.toggle('cws-cur', isCur);
                if (isCur && !cur) cur = sp;
            }
            if (cur && cur !== shown) { shown = cur; this._keepVisible(cur); }
        };
        // The log was scrolled to its bottom when the line was added, which
        // for a long wrapped line is its tail: start by showing its head, so
        // the highlight is on screen from the first character.
        this._keepVisible(spans[from]);
        tick();
        this._cursor = { id: setInterval(tick, CURSOR_MS), spans, from, len: chunk.length };
    }

    // Scroll the log, and only the log, so the character is in view: above
    // the box it comes to the top, below it to the bottom.
    _keepVisible(sp) {
        const log = this._log;
        if (!sp || !log) return;
        const top = sp.offsetTop;   // the log is position:relative, so this is within it
        const bottom = top + sp.offsetHeight;
        if (top < log.scrollTop) log.scrollTop = top;
        else if (bottom > log.scrollTop + log.clientHeight) log.scrollTop = bottom - log.clientHeight;
    }

    // The piece has ended. If it ran its time out, everything in it is
    // sent, whatever the clock says; if it was stopped, the characters
    // after the one under the key were never keyed and stay dim.
    _cursorStop(completed) {
        const c = this._cursor;
        if (!c) return;
        this._cursor = null;
        clearInterval(c.id);
        for (let i = c.from; i < c.from + c.len; i++) {
            const sp = c.spans[i];
            if (!sp) continue;
            sp.classList.remove('cws-cur');
            if (completed) sp.classList.add('cws-done');
        }
    }

    // ── Speed ────────────────────────────────────────────────────────────

    // The same setting as the CW keyer dialog's slider; keep that one in
    // step so the two never disagree about what was last sent.
    async _postSpeed(v) {
        const n = parseInt(v, 10);
        this.setSpeed(n);
        try {
            await fetch('/api/cat/cw/speed', {
                method: 'POST', headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ speed: n }),
            });
        } catch (e) { this._say(`Speed change failed: ${e.message}`, true); }
    }

    // ── Display ──────────────────────────────────────────────────────────

    _logLine(text, state) {
        if (!this._log) return null;
        const row = document.createElement('div');
        row.className = 'cws-line';
        const t = document.createElement('span');
        t.className = 'cws-time';
        t.textContent = new Date().toTimeString().slice(0, 8);
        const body = document.createElement('span');
        body.className = 'cws-text';
        // One span per character so the one under the key can be lit.
        for (const ch of text) {
            const sp = document.createElement('span');
            sp.className = 'cws-ch';
            sp.textContent = ch;
            body.appendChild(sp);
        }
        const tag = document.createElement('span');
        tag.className = 'cws-tag';
        row.append(t, body, tag);
        this._log.appendChild(row);
        this._tag(row, state, state);
        this._log.scrollTop = this._log.scrollHeight;
        return row;
    }

    _tag(row, state, label) {
        if (!row) return;
        row.dataset.state = state;
        const tag = row.querySelector('.cws-tag');
        if (tag) tag.textContent = label;
    }

    _updateQueueStatus() {
        if (!this._running) {
            // A problem reported on the way out outlives the queue; only a
            // clean finish says Ready.
            if (!this._status?.classList.contains('cws-problem')) this._say('Ready.');
            return;
        }
        const waiting = this._queue.length;
        this._say(waiting ? `Sending - ${waiting} more line${waiting === 1 ? '' : 's'} queued.` : 'Sending.');
    }

    _say(text, bad) {
        if (!this._status) return;
        this._status.textContent = text;
        this._status.classList.toggle('cws-problem', !!bad);
    }

    // The keyer dialog's M1-M5 buttons: mark them busy while a typed line
    // is on its way so a press there does not land on top of it. The hook
    // is optional - the page provides it, the module only calls it.
    _setMemButtons(busy) {
        try { window.cwMemButtonsBusy?.(busy); } catch { /* ignore */ }
    }
}
