// radio-panel-icom.js - IWC's adapter for the shared radio panel
// (core/js/radio-panel/radio-panel.js, served at /js/radio-panel/).
//
// The panel is radio-agnostic; everything Icom lives here: which hub
// properties drive which setters, the IC-7300's filter widths, its meter
// scales and its own band scope. YWC's adapter (radio-panel-yaesu.js) is the
// same shape, so a change to one is usually owed to the other.
//
// The IC-7300 has one receiver and two VFOs. Every receiver write goes to the
// active VFO whatever the URL says (the server routes it), so the one key
// group follows the selected VFO, and only the selected VFO box's chips take
// settings. Click a VFO to select it.
//
// The scope is the radio's own (CI-V 27), RF-ordered and centred on the
// operating VFO. Without pseudo-dual it is always sent as 'A', so it is drawn
// in the selected VFO's pane; with pseudo-dual the server tags each pane.
//
// Nothing here transmits. No TUNE and no VOX key: both key the radio, and
// Classic has them. No WSJT-X / JTAlert / Log4OM launcher links and no
// accessibility layer: both were Colin's call on 2026-10-13. Classic keeps them.

import { mountRadioPanel } from '/js/radio-panel/radio-panel.js';
import { alignBins, txMeterFraction, rangeLegends } from '/js/radio-panel/panel-math.js';
import { BAND_PLANS, BAND_EDGES, nearestBandForHz, loadBandPlanFromServer, autoModeForHz, getSegments, segmentForHz } from '/js/ui/band-plan.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';

const BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m', '4m'];
// The IC-7300's modes as IWC shows them (CW-L is CW-R, RTTY-U is RTTY-R,
// DATA-U / DATA-L are USB-D / LSB-D). The mode endpoint takes these names.
const MODES = ['LSB', 'USB', 'CW-U', 'CW-L', 'RTTY-L', 'RTTY-U', 'AM', 'FM', 'DATA-L', 'DATA-U'];
const MODE_LABEL = { 'CW-U': 'CW', 'CW-L': 'CW-R', 'RTTY-L': 'RTTY', 'RTTY-U': 'RTTY-R', 'DATA-L': 'LSB-D', 'DATA-U': 'USB-D' };
const AGC = { '1': 'FAST', '2': 'MID', '3': 'SLOW' };
const AGC_NEXT = { '1': '2', '2': '3', '3': '1' };
const PREAMP = { '0': 'OFF', '1': 'P.AMP1', '2': 'P.AMP2' };
const PREAMP_NEXT = { '0': '1', '1': '2', '2': '0' };
const APF = ['OFF', 'WIDE', 'MID', 'NAR'];
const NOTCH_WIDTH = ['WIDE', 'MID', 'NAR'];
// How to work a concentric knob, appended to each knob's tooltip.
const U = ' - mouse wheel or drag: on the middle for the inner knob, on the ring for the outer';
const STEPS_HZ = [10, 50, 100, 500, 1000, 5000, 10000];
const STEP_KEY = 'iwc.radioPanel.stepHz';
// The scope's spans, full width; the radio takes the ± half-width.
const SPANS_HZ = [5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6];
const SPOT_MAX_AGE_MS = 30 * 60 * 1000;
const WSJTX_POLL_MS = 3000;

const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');
const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const stepText = hz => (hz >= 1000 ? hz / 1000 + ' kHz' : hz + ' Hz');
const pct = x => (x == null ? '--' : Math.round(x / 2.55) + '%');

function post(url, body) {
    const init = { method: 'POST' };
    if (body !== undefined) { init.headers = { 'Content-Type': 'application/json' }; init.body = JSON.stringify(body); }
    return fetch(url, init)
        .then(res => { if (!res.ok) console.warn('radio panel:', url, res.status); return res; })
        .catch(e => console.warn('radio panel:', url, e));
}

// The IC-7300's filter widths per mode, from ic7300-if-width.js (loaded by
// the page before this module), so there is one table.
const widthsForMode = mode => window.IfWidth?.widthsForMode?.(mode) ?? null;
const widthLabel = hz => (hz < 1000 ? hz + ' Hz' : (hz / 1000).toFixed(hz % 1000 ? (hz % 100 ? 2 : 1) : 1) + ' kHz');

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, model, maxPowerW, maxHz, bandPlan, pseudoDual, autoModeChangeOnTune, views, menu }
 */
export function startIcomRadioPanel(root, cfg) {
    const model = cfg.model;
    const maxPowerW = num(cfg.maxPowerW) || 100;
    const maxHz = num(cfg.maxHz) || 74_800_000;
    const pseudoDual = !!cfg.pseudoDual;
    // band-plan.js reads this to decide whether a spectrum click may change mode.
    if (cfg.autoModeChangeOnTune === false) globalThis.iwcAutoModeChangeOnTune = false;
    const edges = () => BAND_EDGES[cfg.bandPlan] || BAND_EDGES.Region1;
    // 4 m only where the region has it.
    const bands = BANDS.filter(b => b !== '4m' || edges().some(e => e.name === '4m'));

    const panel = mountRadioPanel(root, {
        brandShort: 'IWC',
        brandName: 'Icom Web Control',
        model,
        dual: false,
        views: cfg.views ?? [],
        menu: cfg.menu ?? [],
        lamps: [
            { id: 'cat', label: 'CI-V', click: true, title: "The radio's CI-V link: click to disconnect or connect, as Classic's Connect key" },
            { id: 'sdr', label: 'SCOPE', title: "The radio's own band scope" },
            { id: 'dx', label: 'DX' },
        ],
        statusChips: [{ id: 'wsjtx', label: 'WSJT-X TX', title: 'Lit while WSJT-X is transmitting through IWC' }],
        keys: [
            { id: 'mainsub', label: 'VFO A / B', title: 'Select VFO A or VFO B: the keys, knobs and rows act on the selected VFO' },
            { id: 'proc', label: 'COMP', title: 'Speech compressor on / off (transmit audio only)' },
            { id: 'moni', label: 'MONI', title: 'Monitor on / off: hear your own transmit audio. It does not transmit' },
        ],
        rxGroups: {
            knobs: [
                { id: 'afrf', label: 'AF / RF·SQL', turn: true, title: 'Inner: AF gain (volume). Outer: RF gain' + U },
                { id: 'pbt', label: 'TWIN PBT', turn: true, title: 'Inner: PBT inner. Outer: PBT outer. Centre on both is no shift' + U },
                { id: 'notchnr', label: 'NOTCH / NR', turn: true, title: 'Inner: manual notch position. Outer: noise reduction level' + U },
            ],
            keys: [
                { id: 'preamp', label: 'P.AMP', title: 'Preamp: each press steps OFF, P.AMP1, P.AMP2' },
                { id: 'att', label: 'ATT', title: 'Attenuator 20 dB on / off' },
                { id: 'agc', label: 'AGC', title: 'AGC: each press steps FAST, MID, SLOW' },
                { id: 'fil', label: 'FILTER', title: 'Filter: each press steps FIL1, FIL2, FIL3' },
                { id: 'nb', label: 'NB', title: 'Noise blanker on / off' },
                { id: 'nr', label: 'NR', title: 'Noise reduction on / off; the NOTCH / NR outer ring sets its level' },
                { id: 'anf', label: 'AN', title: 'Auto notch on / off' },
                { id: 'notch', label: 'MN', title: 'Manual notch on / off; the NOTCH / NR knob sets its position. Each press while on steps its width' },
                { id: 'apf', label: 'APF', title: 'CW audio peak filter: each press steps OFF, WIDE, MID, NAR' },
            ],
        },
        sharedKnobs: [
            { id: 'micproc', label: 'MIC / COMP', turn: true, title: 'Inner: mic gain. Outer: compressor level' + U },
            { id: 'powermon', label: 'POWER / MONI', turn: true, title: 'Inner: RF power. Outer: monitor level' + U },
            { id: 'speedpitch', label: 'SPEED / PITCH', turn: true, title: 'Inner: CW keyer speed. Outer: CW pitch' + U },
        ],
        clarTurn: true,
        meters: ['PO', 'SWR', 'ALC', 'COMP', 'IDD', 'VDD'],
        meterTitles: { IDD: 'Drain current (Id)', VDD: 'Supply voltage (Vd)' },
        meterScales: {
            PO: ['0', '25', '50', '75', '100'],
            ALC: ['0', '', '', '', 'MAX'],
            IDD: ['0', '', '12.5', '', '25'],
            VDD: rangeLegends(10, 16),
        },
        liveMeters: ['VDD'],
        vfoKeys: [
            { id: 'ab', label: 'A▸B', title: 'Copy VFO A to VFO B' }, { id: 'ba', label: 'B▸A', title: 'Copy VFO B to VFO A' },
            { id: 'swap', label: 'A⇄B', title: 'Swap VFO A and VFO B' },
            { id: 'split', label: 'SPLIT', title: 'Split on / off: receive on one VFO, transmit on the other' },
            { id: 'qsplit', label: 'Q.SPLIT', title: 'Quick split: set the other VFO 5 kHz up and turn split on' },
        ],
        vfoKeyColumns: 3,
        memKeys: [
            { id: 'mstore', label: 'M.STORE', title: "Add the selected VFO to IWC's memory list (the Memories page). It does not write to the radio's own memories" },
        ],
        // CLR zeroes the offset and leaves RIT / ΔTX as they are.
        clarKeys: [
            { id: 'rxclar', label: 'RIT', title: 'RIT on / off: offsets the receive frequency' },
            { id: 'txclar', label: 'ΔTX', title: 'ΔTX on / off: offsets the transmit frequency' },
            { id: 'clrclar', label: 'CLR', title: 'Clear the RIT / ΔTX offset to zero' },
        ],
        bands: bands.map(b => ({ id: b, label: b.replace('m', '') })),
        modes: MODES.map(m => ({ id: m, label: MODE_LABEL[m] ?? m })),
        spectrumPanes: [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SCOPE' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SCOPE' }],
        spectrumSpans: SPANS_HZ.map(hz => ({ hz })),
        frequencyEntry: false,
        clock: true,
        fullscreenKey: true,
        vfoChips: ['seg'],
        onAction: (kind, id, vfo, dir) => act(kind, id, vfo, dir),
        spectrumTuneTo: (hz, vfo) => spectrumTarget(vfo, hz).to,
        choices: (kind, vfo) => choices(kind, vfo),
        storagePrefix: 'iwc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    // ---- the slice of radio state the panel derives things from ----------
    const r = {
        freq: { A: 0, B: 0 }, mode: { A: '', B: '' }, widthHz: { A: null, B: null }, fil: { A: '', B: '' },
        band: { A: '', B: '' },
        att: '00', preamp: '0', nb: '0', nr: '0', anf: '0', notch: '0', notchWidth: 1, apf: 0, agc: '',
        rxClar: false, txClar: false, clarHz: { A: 0, B: 0 },
        notchPos: null, nrLevel: null, pbtIn: null, pbtOut: null,
        af: null, rf: null, sql: null,
        mic: null, procl: null, power: null, mon: null, speed: null, pitch: null,
        tx: false, txMeterRaw: {}, sel: 'A', split: 0, proc: false, moni: false, catOn: false,
    };
    let stepHz = (() => { try { return num(localStorage.getItem(STEP_KEY)); } catch { return 0; } })();
    if (!STEPS_HZ.includes(stepHz)) stepHz = 100;
    panel.setStep(stepText(stepHz));
    panel.setSelected('A');
    const spots = new Map();       // callsign -> { call, hz, at }

    // ---- WSJT-X TX light ----------------------------------------------------
    async function pollWsjtx() {
        let d = null;
        try {
            const res = await fetch('/api/wsjtx/status', { cache: 'no-store' });
            if (res.ok) d = await res.json();
        } catch { /* app restarting: leave it dim */ }
        const title = !d ? 'WSJT-X status unavailable'
            : !d.running ? 'WSJT-X is not running'
            : d.transmitting ? `WSJT-X is transmitting${d.mode ? ' (' + d.mode + ')' : ''}`
            : `WSJT-X is running${d.mode ? ' (' + d.mode + ')' : ''}, not transmitting`;
        panel.setStatusChip('wsjtx', !!d?.transmitting, title);
    }
    pollWsjtx();
    setInterval(pollWsjtx, WSJTX_POLL_MS);

    // Classic's Segment select: the chip names the segment the VFO is in, and
    // picking one sets its mode and then tunes to it.
    function refreshSeg(vfo) {
        const segs = getSegments(cfg.bandPlan, r.band[vfo]);
        const hz = r.freq[vfo];
        if (!segs || !hz) return panel.setChip(vfo, 'seg', '');
        const k = segmentForHz(cfg.bandPlan, r.band[vfo], hz);
        panel.setChip(vfo, 'seg', k ? 'SEG ' + (segs[k]?.label ?? k) : 'SEGMENT');
    }
    async function segmentJump(vfo, key) {
        const sg = getSegments(cfg.bandPlan, r.band[vfo])?.[key];
        if (!sg) return;
        const rx = vfo.toLowerCase();
        if (MODES.includes(sg.mode) && sg.mode !== r.mode[vfo]) await post(`/api/cat/mode/${rx}`, { mode: sg.mode });
        return post(`/api/cat/frequency/${rx}`, { frequencyHz: sg.freq });
    }

    // ---- the concentric knobs: values (turned in knobTurn) -----------------
    const val = x => (x == null ? '--' : String(x));
    const frac = (x, max) => (x == null ? null : Math.max(0, Math.min(1, x / max)));
    const pbtText = x => (x == null ? '--' : (x >= 128 ? '+' : '') + (x - 128));
    // One receiver, one group ('A'), showing the selected VFO.
    function refreshRxKnobs() {
        panel.setGroupKnob('A', 'afrf', {
            inner: frac(r.af, 255), outer: frac(r.rf, 255), text: pct(r.af) + ' · ' + pct(r.rf),
            title: `AF ${pct(r.af)} (inner) · RF ${pct(r.rf)} (outer) · SQL ${pct(r.sql)}` + U,
        });
        panel.setGroupKnob('A', 'pbt', {
            inner: frac(r.pbtIn, 255), outer: frac(r.pbtOut, 255), text: pbtText(r.pbtIn) + ' · ' + pbtText(r.pbtOut),
            title: `PBT inner ${pbtText(r.pbtIn)} · outer ${pbtText(r.pbtOut)} (steps from centre)` + U,
        });
        panel.setGroupKnob('A', 'notchnr', {
            inner: frac(r.notchPos, 255), outer: frac(r.nrLevel, 255),
            text: val(r.notchPos) + ' · ' + pct(r.nrLevel),
            title: `Manual notch ${on(r.notch) ? 'on' : 'off'}, position ${val(r.notchPos)} of 255, width ${NOTCH_WIDTH[r.notchWidth] ?? '--'} (inner) · NR level ${pct(r.nrLevel)} (outer)` + U,
        });
    }
    function refreshSharedKnobs() {
        panel.setGroupKnob('shared', 'micproc', {
            inner: frac(r.mic, 100), outer: frac(r.procl, 100),
            text: val(r.mic) + ' · ' + val(r.procl),
            title: `Mic gain ${val(r.mic)} (inner) · compressor level ${val(r.procl)} (outer)` + U,
        });
        panel.setGroupKnob('shared', 'powermon', {
            inner: frac(r.power, maxPowerW), outer: frac(r.mon, 100),
            text: (r.power == null ? '--' : r.power + ' W') + ' · ' + val(r.mon),
            title: `RF power ${r.power == null ? '--' : r.power + ' W'} (inner) · monitor ${val(r.mon)} (outer)` + U,
        });
        panel.setGroupKnob('shared', 'speedpitch', {
            inner: r.speed == null ? null : (r.speed - 6) / 42, outer: r.pitch == null ? null : (r.pitch - 300) / 600,
            text: val(r.speed) + ' · ' + val(r.pitch),
            title: `Key speed ${val(r.speed)} wpm (inner) · CW pitch ${r.pitch == null ? '--' : r.pitch + ' Hz'} (outer)` + U,
        });
    }

    // The receiver settings are the radio's, not a VFO's: the flags show in
    // the selected VFO's box, where a click on one switches it.
    function refreshFlags() {
        const f = [];
        if (r.preamp && r.preamp !== '0') f.push({ id: 'preamp', label: PREAMP[r.preamp] ?? 'P.AMP' });
        if (on(r.att)) f.push({ id: 'att', label: 'ATT' });
        if (on(r.nb)) f.push({ id: 'nb', label: 'NB' });
        if (on(r.nr)) f.push({ id: 'nr', label: 'NR' });
        if (on(r.anf)) f.push({ id: 'anf', label: 'AN' });
        if (on(r.notch)) f.push({ id: 'notch', label: 'MN' });
        if (r.apf) f.push({ id: 'apf', label: 'APF ' + APF[r.apf] });
        const other = r.sel === 'A' ? 'B' : 'A';
        panel.setFlags(r.sel, f);
        panel.setFlags(other, []);
        panel.setAgc(r.sel, r.agc);
        panel.setAgc(other, '');
        refreshKeys();
        refreshRxKnobs();
    }

    function refreshKeys() {
        panel.setKey('preamp', { on: r.preamp !== '0', sub: PREAMP[r.preamp] ?? '' }, 'A');
        panel.setKey('att', { on: on(r.att), sub: on(r.att) ? '20 dB' : 'OFF' }, 'A');
        panel.setKey('agc', { on: !!r.agc, sub: r.agc }, 'A');
        panel.setKey('fil', { on: !!r.fil[r.sel], sub: r.fil[r.sel] ? 'FIL' + r.fil[r.sel] : '' }, 'A');
        panel.setKey('nb', { on: on(r.nb) }, 'A');
        panel.setKey('nr', { on: on(r.nr) }, 'A');
        panel.setKey('anf', { on: on(r.anf) }, 'A');
        panel.setKey('notch', { on: on(r.notch), sub: on(r.notch) ? NOTCH_WIDTH[r.notchWidth] ?? '' : '' }, 'A');
        panel.setKey('apf', { on: r.apf > 0, sub: r.apf > 0 ? APF[r.apf] : '' }, 'A');
        panel.setKey('mainsub', { on: true, sub: 'VFO-' + r.sel });
    }

    function refreshWidth(vfo) {
        panel.setWidth(vfo, r.widthHz[vfo]);
    }

    // ---- writes ----------------------------------------------------------
    // Receiver writes go to the active VFO whatever the URL says, so only the
    // selected VFO's box takes settings (boxLive).
    const boxLive = vfo => vfo === r.sel;

    function choices(kind, vfo) {
        if (!boxLive(vfo)) return null;
        switch (kind) {
            case 'mode':
                return MODES.map(m => ({ id: m, label: MODE_LABEL[m] ?? m, on: m === r.mode[vfo] }));
            case 'width': {
                const list = widthsForMode(r.mode[vfo]);
                return list?.map(hz => ({ id: String(hz), label: widthLabel(hz), on: hz === r.widthHz[vfo] })) ?? null;
            }
            case 'agc':
                return ['1', '2', '3'].map(c => ({ id: c, label: AGC[c], on: AGC[c] === r.agc }));
            case 'seg': {
                const segs = getSegments(cfg.bandPlan, r.band[vfo]);
                if (!segs) return null;
                const cur = segmentForHz(cfg.bandPlan, r.band[vfo], r.freq[vfo]);
                return Object.entries(segs).map(([k, sg]) => ({ id: k, label: sg.label ?? k, on: k === cur }));
            }
        }
        return null;
    }

    let mstoreTimer = 0;
    function mstoreSays(text) {
        clearTimeout(mstoreTimer);
        panel.setKey('mstore', { on: !!text, sub: text }, 'mem');
        if (text) mstoreTimer = setTimeout(() => mstoreSays(''), 4000);
    }

    function act(kind, id, vfo, dir) {
        // The digit wheel, the dial and the spectrum panes tune either VFO.
        if (vfo && !['tune', 'dial', 'spectrum', 'span'].includes(kind) && !boxLive(vfo)) return;
        const v = vfo || r.sel, rx = v.toLowerCase();
        const flip = x => (on(x) ? '0' : '1');
        switch (kind) {
            case 'width':
                return post(`/api/radio/ifwidth/${rx}`, { hz: parseInt(id, 10) });
            case 'agc':
                return post(`/api/cat/agc/${rx}`, { code: String(id) });
            case 'seg':
                return segmentJump(v, id);
            case 'tune':
                return wheelTune(v, num(id));
            case 'knob':
                return knobTurn(id, v, dir > 0 ? 1 : -1);
            case 'dial':
                return dialTune(v, id === 'up' ? 1 : -1);
            case 'vfo':
                if (id !== r.sel) post(`/api/cat/active-vfo/${id}`);
                return;
            case 'span':
                // One scope: whichever pane's span key, it sets the radio's.
                return post('/api/cat/scopespan/a', Math.round(num(id) / 2));
            case 'spectrum':
                return spectrumTune(v, num(id));
            case 'band':
                return post(`/api/cat/band/${rx}`, { band: id });
            case 'mode':
                return MODES.includes(id) ? post(`/api/cat/mode/${rx}`, { mode: id }) : undefined;
            case 'step':
                if (id === 'cycle') {
                    stepHz = STEPS_HZ[(STEPS_HZ.indexOf(stepHz) + 1) % STEPS_HZ.length];
                    try { localStorage.setItem(STEP_KEY, String(stepHz)); } catch { /* private window */ }
                    panel.setStep(stepText(stepHz));
                    return;
                }
                return tuneBy(id === 'up' ? stepHz : -stepHz);
            case 'key':
                switch (id) {
                    case 'mainsub': return act('vfo', r.sel === 'A' ? 'B' : 'A');
                    case 'preamp': return post(`/api/cat/ipo/${rx}`, { code: PREAMP_NEXT[r.preamp] ?? '0' });
                    case 'att': return post(`/api/cat/attenuator/${rx}`, { code: on(r.att) ? '00' : '20' });
                    case 'agc': return post(`/api/cat/agc/${rx}`, { code: AGC_NEXT[r.agcCode] ?? '2' });
                    case 'fil': {
                        const next = (num(r.fil[v]) % 3) + 1;
                        return post(`/api/radio/filter/${rx}`, { fil: next });
                    }
                    case 'nb': return post(`/api/cat/noiseblanker/${rx}`, { enabled: flip(r.nb) });
                    case 'nr': return post(`/api/cat/nr/${rx}`, { code: flip(r.nr) });
                    case 'anf': return post(`/api/cat/autonotch/${rx}`, { code: flip(r.anf) });
                    // Off > on > each press steps the width > off after NAR.
                    case 'notch':
                        if (!on(r.notch)) return post(`/api/cat/manualnotch/${rx}`, { enabled: '1' });
                        if (r.notchWidth < 2) return post(`/api/cat/manualnotchwidth/${rx}`, { code: String(r.notchWidth + 1) });
                        return post(`/api/cat/manualnotchwidth/${rx}`, { code: '0' })
                            .then(() => post(`/api/cat/manualnotch/${rx}`, { enabled: '0' }));
                    case 'apf': return post(`/api/cat/apf/${rx}`, { width: (r.apf + 1) % 4 });
                    case 'proc': return post('/api/cat/proc', { enabled: !r.proc });
                    case 'moni': return post('/api/cat/monitoron', { on: !r.moni });
                }
                return;
            case 'lamp':
                if (id === 'cat') return post(r.catOn ? '/api/cat/disconnect' : '/api/cat/connect');
                return;
            case 'vfoKey':
                switch (id) {
                    case 'split': return post(`/api/cat/split/${r.split ? 0 : 1}`);
                    case 'qsplit': return post('/api/cat/split/2');
                    case 'ab': return post('/api/cat/copy-vfo/ab');
                    case 'ba': return post('/api/cat/copy-vfo/ba');
                    case 'swap': return post('/api/cat/swap-vfo');
                    case 'rxclar': return post('/api/cat/clarifier', { vfo: v, rxOn: !r.rxClar, txOn: r.txClar, offsetHz: r.clarHz[v] });
                    case 'txclar': return post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: !r.txClar, offsetHz: r.clarHz[v] });
                    case 'clrclar':
                        if (!r.clarHz[v]) return;
                        r.clarHz[v] = 0;
                        refreshClar();
                        return post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: r.txClar, offsetHz: 0 });
                }
                return;
            case 'memKey':
                if (id === 'mstore')
                    return post(`/api/memory/save-vfo/${rx}`, { label: '' }).then(res => mstoreSays(res?.ok ? 'SAVED' : 'FAILED'));
        }
    }

    // ---- the turned knobs ----------------------------------------------
    // Each notch moves the value one step from where the last notch left it,
    // shown at once; writes are folded, so a fast spin sends only the value it
    // stops on. RF power uses Classic's slider endpoint (never radiopower,
    // which switches the radio off).
    const knobQ = {};
    function knobFrom(key, now) {
        const q = knobQ[key];
        return q && (q.busy || Date.now() < q.until) ? q.to : now;
    }
    async function knobSend(key, value, send) {
        const q = knobQ[key] ??= { to: null, busy: false, until: 0 };
        q.to = value;
        q.until = Date.now() + 700;
        if (q.busy) return;
        q.busy = true;
        try {
            let sent = null;
            while (q.to !== sent) { sent = q.to; await send(sent); }
        } finally { q.busy = false; }
    }
    const clampTo = (x, lo, hi) => Math.max(lo, Math.min(hi, x));
    function knobTurn(id, v, dir) {
        const rx = v.toLowerCase();
        // key, current value, step, low, high, show it, send it
        const turn = (key, now, step, lo, hi, show, send) => {
            if (now == null) return;
            const from = knobFrom(key, now);
            const to = clampTo(Math.round((from + dir * step) / step) * step, lo, hi);
            if (to === from) return;
            show(to);
            return knobSend(key, to, send);
        };
        switch (id) {
            case 'afrf:inner':
                return turn('af', r.af, 5, 0, 255, x => { r.af = x; refreshRxKnobs(); },
                    x => post(`/api/cat/afgain/${rx}`, x));
            case 'afrf:outer':
                return turn('rf', r.rf, 5, 0, 255, x => { r.rf = x; refreshRxKnobs(); },
                    x => post(`/api/cat/rfgain/${rx}`, { value: x }));
            case 'pbt:inner':
                return turn('pbtin', r.pbtIn, 1, 0, 255, x => { r.pbtIn = x; refreshRxKnobs(); },
                    x => post('/api/cat/pbt/inner', { value: x }));
            case 'pbt:outer':
                return turn('pbtout', r.pbtOut, 1, 0, 255, x => { r.pbtOut = x; refreshRxKnobs(); },
                    x => post('/api/cat/pbt/outer', { value: x }));
            case 'notchnr:inner':
                return turn('notchpos', r.notchPos, 1, 0, 255, x => { r.notchPos = x; refreshRxKnobs(); },
                    x => post(`/api/cat/manualnotchfreq/${rx}`, { frequencyHz: x }));
            case 'notchnr:outer':
                return turn('nrlevel', r.nrLevel, 5, 0, 255, x => { r.nrLevel = x; refreshRxKnobs(); },
                    x => post(`/api/cat/nrlevel/${rx}`, { level: x }));
            case 'micproc:inner':
                return turn('mic', r.mic, 1, 0, 100, x => { r.mic = x; refreshSharedKnobs(); },
                    x => post('/api/cat/micgain', { value: x }));
            case 'micproc:outer':
                return turn('procl', r.procl, 10, 0, 100, x => { r.procl = x; refreshSharedKnobs(); },
                    x => post('/api/cat/proclevel', { value: x }));
            case 'powermon:inner':
                return turn('power', r.power, 1, 5, maxPowerW, x => { r.power = x; refreshSharedKnobs(); },
                    x => post(`/api/cat/power/${r.sel.toLowerCase()}`, { Watts: x }));
            case 'powermon:outer':
                return turn('mon', r.mon, 1, 0, 100, x => { r.mon = x; refreshSharedKnobs(); },
                    x => post('/api/cat/monitorlevel/a', { level: x }));
            case 'speedpitch:inner':
                return turn('speed', r.speed, 1, 6, 48, x => { r.speed = x; refreshSharedKnobs(); },
                    x => post('/api/cat/cw/speed', { speed: x }));
            case 'speedpitch:outer':
                return turn('pitch', r.pitch, 5, 300, 900, x => { r.pitch = x; refreshSharedKnobs(); },
                    x => post('/api/cat/cwpitch', { hz: x }));
            case 'clar':
                return turn('clar' + v, r.clarHz[v], 10, -9990, 9990, x => { r.clarHz[v] = x; refreshClar(); },
                    x => post('/api/cat/clarifier', { vfo: v, rxOn: r.rxClar, txOn: r.txClar, offsetHz: x }));
        }
    }

    // Twin PBT is read on request rather than carried by the hub: at start,
    // and when the mode (and with it the radio's passband) changes.
    let pbtTimer = 0;
    function readPbt() {
        clearTimeout(pbtTimer);
        pbtTimer = setTimeout(() => {
            fetch('/api/cat/pbt').then(x => (x.ok ? x.json() : null)).then(d => {
                if (!d) return;
                if (!knobQ.pbtin?.busy) r.pbtIn = num(d.inner ?? d.Inner);
                if (!knobQ.pbtout?.busy) r.pbtOut = num(d.outer ?? d.Outer);
                refreshRxKnobs();
            }).catch(() => { /* not connected: stays -- */ });
        }, 400);
    }

    // ---- click-to-tune on the scope ---------------------------------------
    // Classic's rule: the band plan may pick the mode (unless Settings turns
    // that off). IWC's Classic tunes to the clicked frequency in every mode.
    function spectrumTarget(v, hz) {
        const auto = autoModeForHz(hz);
        const mode = auto && MODES.includes(auto) ? auto : null;
        return { mode, to: Math.round(hz) };
    }
    function spectrumTune(v, hz) {
        if (!(hz > 0)) return;
        const { mode, to } = spectrumTarget(v, hz);
        r.freq[v] = to;
        panel.setFrequency(v, to);
        checkBand(v);
        post(`/api/cat/frequency/${v.toLowerCase()}`, { frequencyHz: to });
        if (mode && mode !== r.mode[v]) post(`/api/cat/mode/${v.toLowerCase()}`, { mode });
    }

    // ---- out of band ------------------------------------------------------
    function checkBand(v) {
        const hz = r.freq[v];
        if (!hz) return;
        const inside = edges().some(b => hz >= b.lo && hz <= b.hi);
        const near = inside ? null : nearestBandForHz(hz, edges());
        panel.setOutOfBand(v, inside ? null : 'Outside every amateur band' + (near ? ' - nearest ' + near : ''));
    }
    function publishBandMarks() {
        const ticks = [];
        for (const band of Object.values(BAND_PLANS[cfg.bandPlan] ?? {}))
            for (const [k, sg] of Object.entries(band ?? {}))
                if (sg && typeof sg.freq === 'number') ticks.push({ hz: sg.freq, label: sg.label || k });
        panel.setBandMarks({ edges: edges().flatMap(b => [b.lo, b.hi]), ticks });
    }
    publishBandMarks();
    loadBandPlanFromServer().then(() => { publishBandMarks(); for (const v of ['A', 'B']) { checkBand(v); refreshSeg(v); } });

    function tuneBy(deltaHz) {
        const v = r.sel;
        const cur = r.freq[v];
        if (!cur) return;
        const next = deltaHz > 0 ? Math.floor(cur / stepHz) * stepHz + stepHz : Math.ceil(cur / stepHz) * stepHz - stepHz;
        r.freq[v] = next;
        panel.setFrequency(v, next);
        checkBand(v);
        post(`/api/cat/frequency/${v.toLowerCase()}`, { frequencyHz: next });
    }

    // The dial: one STEP per notch, on the step grid.
    function dialTune(vfo, dir) {
        const cur = wheel[vfo].to ?? r.freq[vfo];
        if (!cur) return;
        const next = dir > 0 ? Math.floor(cur / stepHz) * stepHz + stepHz : Math.ceil(cur / stepHz) * stepHz - stepHz;
        return wheelTune(vfo, next - cur);
    }

    // The wheel over a frequency digit. Notches that come while a write is in
    // flight are folded into the next write; the radio's echo is held off
    // until it has caught up.
    const wheel = { A: { to: null, busy: false, until: 0 }, B: { to: null, busy: false, until: 0 } };
    async function wheelTune(vfo, deltaHz) {
        const w = wheel[vfo];
        const from = w.to ?? r.freq[vfo];
        if (!from || !deltaHz) return;
        const to = from + deltaHz;
        if (to < 30000 || to > maxHz) return;
        w.to = to;
        w.until = Date.now() + 700;
        r.freq[vfo] = to;
        panel.setFrequency(vfo, to);
        checkBand(vfo);
        if (w.busy) return;
        w.busy = true;
        try {
            let sent = null;
            while (w.to !== sent) {
                sent = w.to;
                await post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: sent });
            }
        } finally {
            w.busy = false;
            setTimeout(() => { if (!w.busy && Date.now() >= w.until) w.to = null; }, 700);
        }
    }
    const wheelHolds = vfo => wheel[vfo].busy || Date.now() < wheel[vfo].until;

    function refreshClar() {
        const hz = r.clarHz[r.sel];
        const txt = (hz >= 0 ? '+' : '') + hz + ' Hz';
        panel.setClarifier(r.rxClar || r.txClar ? txt : 'OFF ' + txt, 0.5 + hz / 19980);
        panel.setKey('rxclar', { on: r.rxClar });
        panel.setKey('txclar', { on: r.txClar });
    }

    // The IC-7300's own meter scales: 100 W, Id to 25 A, Vd 10-16 V.
    function refreshTxMeters() {
        const raw = r.txMeterRaw;
        const clamp = x => Math.max(0, Math.min(1, x));
        panel.setTxMeter('PO', clamp(calibrateNumeric('PWR', num(raw.PO)) / maxPowerW));
        panel.setTxMeter('SWR', txMeterFraction('SWR', calibrateNumeric('SWR', num(raw.SWR))));
        panel.setTxMeter('ALC', clamp(calibrateNumeric('ALC', num(raw.ALC)) / 100));
        panel.setTxMeter('COMP', txMeterFraction('COMP', Math.max(0, Math.min(20, calibrateNumeric('Compression', num(raw.COMP))))));
        panel.setTxMeter('IDD', clamp(calibrateNumeric('IDD', num(raw.IDD)) / 25));
    }

    // ---- the scope ---------------------------------------------------------
    const scopeState = { A: '', B: '' };
    const SCOPE_MSG = {
        streaming: null, connecting: 'Scope starting…', unconfigured: 'Scope switched off',
        disconnected: 'Radio not connected', blocked: 'The radio is not sending scope data',
        outofrange: 'This VFO is outside the scope span',
    };
    function refreshScopeLamp() {
        const sts = Object.values(scopeState);
        panel.setLamp('sdr', sts.includes('streaming') ? 'on' : sts.includes('connecting') ? 'warn'
            : sts.every(x => x === '' || x === 'unconfigured') ? 'off' : 'bad');
    }
    // Without pseudo-dual the server tags everything 'A' and the sweep is
    // centred on the selected VFO, so it goes to the selected VFO's pane, and
    // the other pane says why it is empty.
    const paneFor = id => (pseudoDual ? id : r.sel);
    function showIdlePane() {
        if (pseudoDual) return;
        const other = r.sel === 'A' ? 'B' : 'A';
        panel.clearSpectrum(other);
        panel.setSpectrumSource({ label: 'SCOPE', status: 'The scope follows the selected VFO (pseudo-dual is off in Settings)' }, other);
    }
    function scopeStatus(id, st, detail) {
        const pane = paneFor(id);
        scopeState[pseudoDual ? id : 'A'] = st;
        refreshScopeLamp();
        panel.setSpectrumSource({ label: 'SCOPE', status: detail || (st in SCOPE_MSG ? SCOPE_MSG[st] : st) }, pane);
    }

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
        panel.setSpots([...spots.values()].sort((a, b) => b.at - a.at).slice(0, 40));
    }
    setInterval(publishSpots, 60_000);

    // ---- one hub, one router ---------------------------------------------
    // The receiver-wide settings arrive as A and B with the same value (the
    // server mirrors them), so the A copy is the one read.
    const handlers = {
        IsConnected: v => { r.catOn = on(v); panel.setLamp('cat', r.catOn ? 'on' : 'bad'); if (r.catOn) readPbt(); },
        FrequencyA: v => { if (wheelHolds('A')) return; r.freq.A = num(v); panel.setFrequency('A', r.freq.A); checkBand('A'); refreshSeg('A'); },
        FrequencyB: v => { if (wheelHolds('B')) return; r.freq.B = num(v); panel.setFrequency('B', r.freq.B); checkBand('B'); refreshSeg('B'); },
        BandA: v => { if (v !== r.band.A) { r.band.A = v; panel.clearSpectrum('A'); } panel.setBand(r.band[r.sel]); refreshSeg('A'); },
        BandB: v => { if (v !== r.band.B) { r.band.B = v; panel.clearSpectrum('B'); } panel.setBand(r.band[r.sel]); refreshSeg('B'); },
        ModeA: v => { const m = String(v ?? ''); if (m !== r.mode.A && r.sel === 'A') readPbt(); r.mode.A = m; panel.setMode('A', MODE_LABEL[m] ?? m); refreshWidth('A'); },
        ModeB: v => { const m = String(v ?? ''); if (m !== r.mode.B && r.sel === 'B') readPbt(); r.mode.B = m; panel.setMode('B', MODE_LABEL[m] ?? m); refreshWidth('B'); },
        IfWidthA: v => { r.widthHz.A = v === '' || v == null ? null : num(v); refreshWidth('A'); },
        IfWidthB: v => { r.widthHz.B = v === '' || v == null ? null : num(v); refreshWidth('B'); },
        SelectedFilterA: v => { r.fil.A = String(v ?? ''); refreshKeys(); },
        SelectedFilterB: v => { r.fil.B = String(v ?? ''); refreshKeys(); },
        AgcA: v => { r.agcCode = String(v); r.agc = AGC[String(v)] ?? ''; refreshFlags(); },
        ActiveVfo: v => {
            r.sel = num(v) === 1 ? 'B' : 'A';
            panel.setSelected(r.sel);
            panel.setBand(r.band[r.sel]);
            refreshFlags();
            refreshClar();
            showIdlePane();
            readPbt();
        },
        IpoA: v => { r.preamp = String(v); refreshFlags(); },
        AttA: v => { r.att = String(v); refreshFlags(); },
        NbA: v => { r.nb = String(v); refreshFlags(); },
        NrA: v => { r.nr = String(v); refreshFlags(); },
        NrLevelA: v => { r.nrLevel = num(v); refreshRxKnobs(); },
        AutoNotchA: v => { r.anf = String(v); refreshFlags(); },
        ManualNotchA: v => { r.notch = String(v); refreshFlags(); },
        ManualNotchFreqA: v => { r.notchPos = num(v); refreshRxKnobs(); },
        ManualNotchWidthA: v => { r.notchWidth = num(v); refreshKeys(); refreshRxKnobs(); },
        ApfWidthA: v => { r.apf = num(v); refreshFlags(); },
        SplitMode: v => { r.split = num(v); panel.setSplit(r.split !== 0); panel.setKey('split', { on: r.split !== 0 }); },
        RxClarOn: v => { r.rxClar = on(v); refreshClar(); },
        TxClarOn: v => { r.txClar = on(v); refreshClar(); },
        ClarifierOffsetA: v => { r.clarHz.A = num(v); refreshClar(); },
        ClarifierOffsetB: v => { r.clarHz.B = num(v); refreshClar(); },
        IsTransmitting: v => { r.tx = on(v); panel.setTx(r.tx); },
        ProcEnabled: v => { r.proc = on(v); panel.setKey('proc', { on: r.proc }); refreshSharedKnobs(); },
        MonitorOn: v => { r.moni = on(v); panel.setKey('moni', { on: r.moni }); },
        AfGainA: v => { r.af = num(v); refreshRxKnobs(); },
        RfGainA: v => { r.rf = num(v); refreshRxKnobs(); },
        SquelchA: v => { r.sql = num(v); refreshRxKnobs(); },
        CwPitch: v => { r.pitch = num(v); refreshSharedKnobs(); },
        CwSpeed: v => { r.speed = num(v); refreshSharedKnobs(); },
        Power: v => { r.power = num(v); refreshSharedKnobs(); },
        MicGain: v => { r.mic = num(v); refreshSharedKnobs(); },
        ProcLevel: v => { r.procl = num(v); refreshSharedKnobs(); },
        MonitorLevelA: v => { r.mon = num(v); refreshSharedKnobs(); },
        SMeterA: v => panel.setSMeter(r.sel, calibrateNumeric('SMETER', num(v))),
        PowerMeter: v => { r.txMeterRaw.PO = typeof v === 'object' && v ? v.value : v; refreshTxMeters(); },
        SWRMeter: v => { r.txMeterRaw.SWR = v; refreshTxMeters(); },
        ALCMeter: v => { r.txMeterRaw.ALC = v; refreshTxMeters(); },
        CompressionMeter: v => { r.txMeterRaw.COMP = v; refreshTxMeters(); },
        IDDMeter: v => { r.txMeterRaw.IDD = v; refreshTxMeters(); },
        // Raw 0 is no reading; a Vd off the 10-16 V dial is a bad read.
        VDDMeter: v => {
            const raw = num(v);
            if (raw <= 0) return;
            const volts = calibrateNumeric('VPA', raw);
            if (volts >= 10 && volts <= 16) panel.setTxMeter('VDD', txMeterFraction('VDD', volts, { min: 10, max: 16 }));
        },
        SdrStatus: v => {
            const id = v?.sdrId;
            if (id !== 'A' && id !== 'B') return;
            scopeStatus(id, String(v.status ?? ''));
        },
        SdrError: v => {
            const id = v?.sdrId;
            if ((id === 'A' || id === 'B') && v.detail) scopeStatus(id, 'blocked', String(v.detail));
        },
        SpectrumUpdate: v => {
            const id = v?.sdrId;
            if ((id !== 'A' && id !== 'B') || !v.bins?.length) return;
            const pane = paneFor(id);
            const span = num(v.spanHz);
            const key = pseudoDual ? id : 'A';
            if (scopeState[key] !== 'streaming') { scopeState[key] = 'streaming'; refreshScopeLamp(); }
            panel.setSpectrumSource({ label: 'SCOPE', spanHz: span, status: null }, pane);
            // RF-ordered already; shift so the pane's dial sits in the middle.
            let bins = v.bins;
            const dial = r.freq[pane];
            if (dial > 0 && num(v.centreHz) > 0) bins = alignBins(bins, num(v.centreHz) - dial, span);
            panel.pushSpectrum(bins, pane);
        },
        DxSpot: addSpot,
        DxClusterStatus: v => {
            const st = String(v?.status ?? '').toLowerCase();
            panel.setLamp('dx', st === 'connected' ? 'on' : st === 'connecting' ? 'warn' : st === 'off' || !st ? 'off' : 'bad');
        },
    };

    loadFromBackend({ 'S-Meter': ['SMETER', 'SMETER_LABELS'] });
    fetch('/api/dxcluster/spots').then(x => (x.ok ? x.json() : [])).then(list => { for (const sp of list ?? []) addSpot(sp); }).catch(() => {});
    fetch('/api/dxcluster/status').then(x => (x.ok ? x.json() : null)).then(s => { if (s) handlers.DxClusterStatus(s); }).catch(() => {});
    showIdlePane();
    readPbt();

    const conn = cfg.conn ?? window.rwcHubConnection('/radioHub');
    conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('radio panel:', msg.property, e); }
    });
    conn.onreconnecting(() => panel.setLamp('cat', 'warn'));
    conn.onclose(() => panel.setLamp('cat', 'bad'));
    panel.setLamp('cat', 'warn');
    conn.start().catch(() => panel.setLamp('cat', 'bad'));

    return panel;
}
