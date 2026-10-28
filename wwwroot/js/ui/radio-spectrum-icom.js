// radio-spectrum-icom.js - IWC's feed for the radio panel's pop-out spectrum
// (core/js/radio-panel/spectrum-popout.js), the window at /RadioSpectrum.
//
// The scope itself is spectrum-feed-icom.js, shared with the RTTY Contest
// view. The span picker sets the radio's scope span, a click on a pane tunes
// that VFO as a click on the board does (spectrum-tune-icom.js), and the
// mouse wheel steps it by the board's STEP.
// With X-WATCH off and the VFOs on different bands the watch pane is hidden,
// as on the board (collapseWatch there).

import { mountSpectrumPopout } from '/js/radio-panel/spectrum-popout.js';
import { spanText } from '/js/radio-panel/panel-math.js';
import { reportSpectrumPanes } from '/js/ui/spectrum-panes-report.js';
import { dialStepper } from '/js/radio-panel/dial-stepper.js';
import { post, startIcomSpectrumFeed } from '/js/ui/spectrum-feed-icom.js';

const SPANS_HZ = [5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6];
const XWATCH_OFF_TEXT = 'Cross-band watch is off: X-WATCH switches it on';

// The board's STEP (radio-panel-icom.js), saved in this browser.
const STEP_KEY = 'iwc.radioPanel.stepHz';
const STEPS_HZ = [10, 50, 100, 500, 1000, 5000, 10000];

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
function boardStepHz() {
    let hz = 0;
    try { hz = num(localStorage.getItem(STEP_KEY)); } catch { /* private window */ }
    return STEPS_HZ.includes(hz) ? hz : 100;
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, popout, pseudoDual, autoModeChangeOnTune }  popout: the page's PopoutChild
 */
export function startIcomSpectrumPopout(root, cfg) {
    const pseudoDual = !!cfg.pseudoDual;
    // band-plan.js reads this to decide whether a click may change mode.
    if (cfg.autoModeChangeOnTune === false) globalThis.iwcAutoModeChangeOnTune = false;
    const panes = [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SCOPE' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SCOPE' }];

    const pop = mountSpectrumPopout(root, {
        title: 'SPECTRUM',
        panes,
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        onSpan: (id, hz) => feed.setSpan(id, hz),
        onTune: (vfo, hz) => feed.tune(vfo, hz),
        onStep: (vfo, dir) => stepper.turn(vfo, dir),
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'iwc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    // The tab collapseWatch chose, and the one it replaced: { was, now }.
    let forcedView = null;
    const feed = startIcomSpectrumFeed({
        conn: cfg.conn,
        pseudoDual,
        pane: vfo => pop.pane(vfo),
        holds: vfo => stepper.holds(vfo),
        onSpan: (vfo, hz) => pop.setSpanValue(vfo, hz),
        statusText: (vfo, text) => (watchCollapsed() && vfo === watchVfo() ? XWATCH_OFF_TEXT : text),
        onChange: () => collapseWatch(),
    });
    const r = feed.state;
    const stepper = dialStepper({
        stepHz: boardStepHz,
        freq: vfo => r.freq[vfo],
        show: (vfo, hz) => feed.showFrequency(vfo, hz),
        write: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
        maxHz: num(cfg.maxHz) || 74_800_000,
    });

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
            if (!forcedView) {
                if (view !== r.sel) { forcedView = { was: view, now: r.sel }; pop.setView(r.sel); }
            } else if (view === forcedView.now && view !== r.sel) {
                // Listening moved to the other VFO: follow it.
                forcedView.now = r.sel;
                pop.setView(r.sel);
            }
            pop.pane(watchVfo()).clear();
            pop.pane(watchVfo()).setStatus(XWATCH_OFF_TEXT);
        } else if (forcedView) {
            // Unless the operator has picked a tab since.
            if (view === forcedView.now) pop.setView(forcedView.was);
            forcedView = null;
        }
    }

    const conn = cfg.conn;
    conn.start().catch(e => console.warn('spectrum: hub', e));
    reportSpectrumPanes(conn, () => ({
        A: pop.view === 'A' || pop.view === 'both',
        B: pop.view === 'B' || pop.view === 'both',
    }));
    return pop;
}
