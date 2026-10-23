// radio-spectrum-icom.js - IWC's feed for the radio panel's pop-out spectrum
// (core/js/radio-panel/spectrum-popout.js), the window at /RadioSpectrum.
//
// The source is the radio's own scope, as on the board (radio-panel-icom.js):
// its bins arrive RF-ordered, so the only correction is shifting them so the
// dial sits in the middle. Without pseudo-dual the server tags every frame
// 'A' and the sweep follows the selected VFO, so frames go to that VFO's
// pane. The span picker sets the radio's scope span - the one write here.
// With X-WATCH off and the VFOs on different bands the watch pane is hidden,
// as on the board (collapseWatch there).

import { mountSpectrumPopout } from '/js/radio-panel/spectrum-popout.js';
import { alignBins, spanText } from '/js/radio-panel/panel-math.js';
import { reportSpectrumPanes } from '/js/ui/spectrum-panes-report.js';

const SPANS_HZ = [5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6];
const SPOT_MAX_AGE_MS = 30 * 60 * 1000;
const STATUS_TEXT = {
    streaming: null, connecting: 'Scope starting…', unconfigured: 'Scope switched off',
    disconnected: 'Radio not connected', blocked: 'The radio is not sending scope data',
    outofrange: 'This VFO is outside the scope span',
};
const XWATCH_OFF_TEXT = 'Cross-band watch is off: X-WATCH switches it on';
// Out of the sweep with X-WATCH on: the next peek fills it.
const WAITING_TEXT = 'Waiting for data';

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, popout, pseudoDual }  popout: the page's PopoutChild
 */
export function startIcomSpectrumPopout(root, cfg) {
    const pseudoDual = !!cfg.pseudoDual;
    const panes = [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SCOPE' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SCOPE' }];

    const pop = mountSpectrumPopout(root, {
        title: 'SPECTRUM',
        panes,
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        // One scope: whichever pane's picker, it sets the radio's span (+/- half).
        onSpan: (_id, hz) => {
            fetch('/api/cat/scopespan/a', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(Math.round(hz / 2)),
            })
                .then(res => { if (!res.ok) console.warn('spectrum: span', res.status); })
                .catch(e => console.warn('spectrum: span', e));
        },
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'iwc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    const r = {
        sel: 'A', freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null },
        band: { A: '', B: '' },
        xwatch: true,          // until the hub says (it does on connect)
        scope: { A: '', B: '' },   // the last SdrStatus for each
        forcedView: null,      // { was, now }: the tab collapseWatch chose, and the one it replaced
    };
    const paneFor = id => (pseudoDual ? id : r.sel);
    const refreshPassband = vfo => pop.pane(vfo).setPassband(r.mode[vfo], r.widthHz[vfo]);

    function showIdlePane() {
        if (pseudoDual) return;
        const other = r.sel === 'A' ? 'B' : 'A';
        pop.pane(other).clear();
        pop.pane(other).setStatus('The scope follows the selected VFO (pseudo-dual is off in Settings)');
    }

    // Cross-band watch off with the VFOs on different bands: the watch pane
    // could only say it was out of range, so only the VFO you hear shows,
    // and your own tab comes back when the bands meet or X-WATCH comes on.
    const watchVfo = () => (r.sel === 'A' ? 'B' : 'A');
    const crossBand = () => pseudoDual && !!r.band.A && !!r.band.B && r.band.A !== r.band.B;
    const watchCollapsed = () => crossBand() && !r.xwatch;
    // The VFO you are not hearing is refreshed only by the peeks, on one band
    // or two, one waterfall row each: its pane shows the trace alone.
    const watchPeeked = () => (pseudoDual ? watchVfo() : null);
    function collapseWatch() {
        if (!pseudoDual) return;
        for (const v of ['A', 'B']) pop.pane(v).setWaterfall(v !== watchPeeked());
        const view = pop.view;
        if (watchCollapsed()) {
            if (!r.forcedView) {
                if (view !== r.sel) { r.forcedView = { was: view, now: r.sel }; pop.setView(r.sel); }
            } else if (view === r.forcedView.now && view !== r.sel) {
                // Listening moved to the other VFO: follow it.
                r.forcedView.now = r.sel;
                pop.setView(r.sel);
            }
            pop.pane(watchVfo()).clear();
            pop.pane(watchVfo()).setStatus(XWATCH_OFF_TEXT);
        } else if (r.forcedView) {
            // Unless the operator has picked a tab since.
            if (view === r.forcedView.now) pop.setView(r.forcedView.was);
            r.forcedView = null;
        }
    }
    const statusText = (vfo, text) => (watchCollapsed() && vfo === watchVfo() ? XWATCH_OFF_TEXT : text);
    function scopeStatus(id, st) {
        r.scope[id] = st;
        const vfo = paneFor(id);
        pop.pane(vfo).setStatus(statusText(vfo, st === 'outofrange' && r.xwatch ? WAITING_TEXT : st in STATUS_TEXT ? STATUS_TEXT[st] : st));
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
        for (const p of panes) pop.pane(p.id).setSpots(list);
    }
    setInterval(publishSpots, 60_000);

    const perVfo = vfo => ({
        ['Frequency' + vfo]: v => { r.freq[vfo] = num(v); pop.pane(vfo).setFrequency(r.freq[vfo]); },
        ['Mode' + vfo]: v => { r.mode[vfo] = String(v ?? ''); refreshPassband(vfo); },
        ['IfWidth' + vfo]: v => { r.widthHz[vfo] = v === '' || v == null ? null : num(v); refreshPassband(vfo); },
        ['Band' + vfo]: v => { r.band[vfo] = String(v ?? ''); pop.pane(vfo).clear(); collapseWatch(); },
    });
    const handlers = {
        ...perVfo('A'),
        ...perVfo('B'),
        ActiveVfo: v => { r.sel = num(v) === 1 ? 'B' : 'A'; showIdlePane(); collapseWatch(); },
        CrossBandWatch: v => {
            r.xwatch = !!v?.enabled;
            collapseWatch();
            // Out of range says Waiting for data or not by X-WATCH.
            for (const id of ['A', 'B']) if (r.scope[id] === 'outofrange') scopeStatus(id, 'outofrange');
        },
        SdrStatus: v => {
            const id = v?.sdrId;
            if (id === 'A' || id === 'B') scopeStatus(id, String(v.status ?? ''));
        },
        SdrError: v => {
            const id = v?.sdrId;
            if ((id === 'A' || id === 'B') && v.detail) pop.pane(paneFor(id)).setStatus(statusText(paneFor(id), String(v.detail)));
        },
        SpectrumUpdate: v => {
            const id = v?.sdrId;
            if ((id !== 'A' && id !== 'B') || !v.bins?.length) return;
            const vfo = paneFor(id);
            const pane = pop.pane(vfo);
            const span = num(v.spanHz);
            pane.setStatus(null);
            pane.setSpan(span);
            pop.setSpanValue(vfo, span);
            let bins = v.bins;
            const dial = r.freq[vfo];
            if (dial > 0 && num(v.centreHz) > 0) bins = alignBins(bins, num(v.centreHz) - dial, span);
            pane.push(bins);
        },
        DxSpot: addSpot,
    };

    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});
    showIdlePane();

    const conn = cfg.conn;
    conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('spectrum:', msg.property, e); }
    });
    conn.start().catch(e => console.warn('spectrum: hub', e));
    reportSpectrumPanes(conn, () => ({
        A: pop.view === 'A' || pop.view === 'both',
        B: pop.view === 'B' || pop.view === 'both',
    }));
    return pop;
}
