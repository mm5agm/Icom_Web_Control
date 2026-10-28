// spectrum-feed-icom.js - IWC's scope feed for the shared spectrum panes
// (core/js/radio-panel/spectrum-pane.js), for every page that shows them on
// their own: the pop-out spectrum (radio-spectrum-icom.js) and the RTTY
// Contest view (rtty-contest-icom.js).
//
// The source is the radio's own scope, as on the board (radio-panel-icom.js):
// its bins arrive RF-ordered, so the only correction is shifting them so the
// dial sits in the middle. Without pseudo-dual the server tags every frame
// 'A' and the sweep follows the selected VFO, so frames go to that VFO's
// pane. A click tunes as a click on the board does (spectrum-tune-icom.js)
// unless the page says otherwise.
//
// It listens on the page's hub connection but does not start it: the page
// may have handlers of its own to add first.

import { alignBins } from '/js/radio-panel/panel-math.js';
import { loadBandPlanFromServer } from '/js/ui/band-plan.js';
import { spectrumTarget } from '/js/ui/spectrum-tune-icom.js';

const SPOT_MAX_AGE_MS = 30 * 60 * 1000;
const STATUS_TEXT = {
    streaming: null, connecting: 'Scope starting…', unconfigured: 'Scope switched off',
    disconnected: 'Radio not connected', blocked: 'The radio is not sending scope data',
    outofrange: 'This VFO is outside the scope span',
};
// Out of the sweep with X-WATCH on: the next peek fills it.
const WAITING_TEXT = 'Waiting for data';
const IDLE_TEXT = 'The scope follows the selected VFO (pseudo-dual is off in Settings)';

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

export function post(url, body) {
    return fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
        .then(res => { if (!res.ok) console.warn('spectrum:', url, res.status); return res; })
        .catch(e => console.warn('spectrum:', url, e));
}

/**
 * @param {object} cfg
 * @param {object} cfg.conn  the page's hub connection, not yet started
 * @param {boolean} [cfg.pseudoDual]
 * @param {(vfo:string) => object|undefined} cfg.pane  the SpectrumPane showing that VFO, if any
 * @param {(vfo:string, hz:number) => void} [cfg.onSpan]  a frame arrived at this span
 * @param {(vfo:string) => boolean} [cfg.holds]  true while the page's own dial write is in flight
 * @param {(vfo:string, hz:number, mode:string) => {mode:string|null, to:number}} [cfg.target]
 *        where a click at hz tunes; the board's rule (spectrumTarget) if left out
 * @param {(vfo:string, text:string|null) => string|null} [cfg.statusText]  the page's say
 *        over a pane's status line (the pop-out's X-WATCH off)
 * @param {(property:string) => void} [cfg.onChange]  after ActiveVfo, Band* or CrossBandWatch
 */
export function startIcomSpectrumFeed(cfg) {
    const pseudoDual = !!cfg.pseudoDual;
    const pane = vfo => cfg.pane(vfo);
    const target = cfg.target ?? ((_vfo, hz) => spectrumTarget(hz));
    const statusText = cfg.statusText ?? ((_vfo, text) => text);
    const changed = p => cfg.onChange?.(p);
    loadBandPlanFromServer();

    const r = {
        sel: 'A', freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null },
        band: { A: '', B: '' },
        xwatch: true,          // until the hub says (it does on connect)
        scope: { A: '', B: '' },   // the last SdrStatus for each
    };
    const paneFor = id => (pseudoDual ? id : r.sel);
    const refreshPassband = vfo => pane(vfo)?.setPassband(r.mode[vfo], r.widthHz[vfo]);

    function showIdlePane() {
        if (pseudoDual) return;
        const other = r.sel === 'A' ? 'B' : 'A';
        pane(other)?.clear();
        pane(other)?.setStatus(statusText(other, IDLE_TEXT));
    }
    function scopeStatus(id, st) {
        r.scope[id] = st;
        const vfo = paneFor(id);
        const text = st === 'outofrange' && r.xwatch ? WAITING_TEXT : st in STATUS_TEXT ? STATUS_TEXT[st] : st;
        pane(vfo)?.setStatus(statusText(vfo, text));
    }

    const spots = new Map();       // callsign -> { call, hz, at }
    function addSpot(sp) {
        const call = sp?.callsign ?? sp?.Callsign;
        const hz = num(sp?.frequencyHz ?? sp?.FrequencyHz);
        if (!call || !hz) return;
        const at = Date.parse(sp.receivedUtc ?? sp.ReceivedUtc ?? '') || Date.now();
        spots.set(call, { call, hz, at });
        publishSpots();
    }
    function publishSpots() {
        const cutoff = Date.now() - SPOT_MAX_AGE_MS;
        for (const [k, sp] of spots) if (sp.at < cutoff) spots.delete(k);
        const list = [...spots.values()].sort((a, b) => b.at - a.at).slice(0, 40);
        for (const v of ['A', 'B']) pane(v)?.setSpots(list);
    }
    setInterval(publishSpots, 60_000);

    const perVfo = vfo => ({
        ['Frequency' + vfo]: v => { if (cfg.holds?.(vfo)) return; r.freq[vfo] = num(v); pane(vfo)?.setFrequency(r.freq[vfo]); },
        ['Mode' + vfo]: v => { r.mode[vfo] = String(v ?? ''); refreshPassband(vfo); },
        ['IfWidth' + vfo]: v => { r.widthHz[vfo] = v === '' || v == null ? null : num(v); refreshPassband(vfo); },
        ['Band' + vfo]: v => { r.band[vfo] = String(v ?? ''); pane(vfo)?.clear(); changed('Band' + vfo); },
    });
    const handlers = {
        ...perVfo('A'),
        ...perVfo('B'),
        ActiveVfo: v => { r.sel = num(v) === 1 ? 'B' : 'A'; showIdlePane(); changed('ActiveVfo'); },
        CrossBandWatch: v => {
            r.xwatch = !!v?.enabled;
            changed('CrossBandWatch');
            // Out of range says Waiting for data or not by X-WATCH.
            for (const id of ['A', 'B']) if (r.scope[id] === 'outofrange') scopeStatus(id, 'outofrange');
        },
        SdrStatus: v => {
            const id = v?.sdrId;
            if (id === 'A' || id === 'B') scopeStatus(id, String(v.status ?? ''));
        },
        SdrError: v => {
            const id = v?.sdrId;
            if ((id === 'A' || id === 'B') && v.detail) pane(paneFor(id))?.setStatus(statusText(paneFor(id), String(v.detail)));
        },
        SpectrumUpdate: v => {
            const id = v?.sdrId;
            if ((id !== 'A' && id !== 'B') || !v.bins?.length) return;
            const vfo = paneFor(id);
            const p = pane(vfo);
            if (!p) return;
            const span = num(v.spanHz);
            p.setStatus(null);
            p.setSpan(span);
            cfg.onSpan?.(vfo, span);
            let bins = v.bins;
            const dial = r.freq[vfo];
            if (dial > 0 && num(v.centreHz) > 0) bins = alignBins(bins, num(v.centreHz) - dial, span);
            p.push(bins);
        },
        DxSpot: addSpot,
    };

    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});
    showIdlePane();

    cfg.conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('spectrum:', msg.property, e); }
    });

    return {
        /** What the feed has heard: sel, freq, mode, ... Read only. */
        state: r,
        /** Where the dial lands for a click at hz. */
        tuneTo: (vfo, hz) => target(vfo, hz, r.mode[vfo]).to,
        /** A click at hz: tune there, and change mode if the target says to. */
        tune(vfo, hz) {
            if (!(hz > 0)) return;
            const { mode, to } = target(vfo, hz, r.mode[vfo]);
            post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: to });
            if (mode && mode !== r.mode[vfo]) post(`/api/cat/mode/${vfo.toLowerCase()}`, { mode });
        },
        /** Draw the VFO at hz now, ahead of the radio's echo (a dial step). */
        showFrequency(vfo, hz) { r.freq[vfo] = hz; pane(vfo)?.setFrequency(hz); },
        /** One scope: whichever pane asks, it sets the radio's span (+/- half). */
        setSpan(_vfo, hz) { post('/api/cat/scopespan/a', Math.round(hz / 2)); },
    };
}
