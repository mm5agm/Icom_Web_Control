// rtty-contest-icom.js - IWC's feed for the RTTY Contest view
// (core/js/rtty-contest/contest-view.js), the page at /rtty-contest.
//
// The spectrum is the pop-out spectrum's (spectrum-feed-icom.js): the
// radio's own scope, which follows the selected VFO. So VFO-A and VFO-B here
// are the radio's two VFOs, and picking one selects it on the radio, as the
// VFO keys on the Radio panel do; a change on the radio moves the view with
// it. Only the VFO on screen is reported as showing, so the cross-band peek
// never dips the audio under the tuner.
//
// This adds the strip: frequency and mode of the VFO on screen, the tuner's
// Mark/Shift, the S meter, and PO, SWR and ALC while transmitting. The band
// plan never changes the mode on a click here (#169): the page sets
// iwcAutoModeChangeOnTune false before this loads.
//
// A click puts the signal where the tuner listens. In RTTY and RTTY-R alike
// the IC-7300 shows the higher tone of the pair (checked on DDK9,
// 2026-10-28), so a click between the two traces lands the dial half the
// shift above the click in both. In DATA the dial is the carrier, so the
// click goes the tuner's Mark/Shift midpoint away from it: below the click
// in DATA-U, above it in DATA-L. Either way the pair lands on the tuner's
// Mark, which is where Auto puts it too in a DATA mode the tuner holds.
//
// The band keys land on the band's RTTY spot (the band plan's RTTY segment,
// else RTTY_SPOTS below) in the operator's RTTY mode. A band with no spot
// (60 m, 4 m) goes to the radio's own band memory, then to that mode.
//
// The RTTY mode is RTTY-L, RTTY-U, DATA-L or DATA-U, kept in Settings
// (RttyTunerMode) and picked on the strip's MODE list, which shows the mode
// of the VFO on screen. The tuner holds the radio in it while it is open,
// so AFSK has a DATA mode the tuner will leave alone, as on YWC.
//
// Under the tuner, PWR and WIDTH. There is no SHIFT: the IC-7300 has Twin
// PBT instead of an IF shift.

import { mountContestView } from '/js/rtty-contest/contest-view.js';
import { spanText, sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { dialStepper } from '/js/radio-panel/dial-stepper.js';
import { calibrateNumeric, loadFromBackend } from '/js/calibration/calibration-engine.js';
import { afskMidpointAudioHz, loadRttySettings } from '/js/rtty/rtty-settings.js';
import { post, startIcomSpectrumFeed } from '/js/ui/spectrum-feed-icom.js';
import { reportSpectrumPanes } from '/js/ui/spectrum-panes-report.js';
import { BAND_EDGES, getSegments } from '/js/ui/band-plan.js';

const SPANS_HZ = [5e3, 10e3, 20e3, 50e3, 100e3, 200e3, 500e3, 1e6];

// The bands, as the band keys on the Radio panel have them.
const BANDS = ['160m', '80m', '60m', '40m', '30m', '20m', '17m', '15m', '12m', '10m', '6m', '4m'];

// Where RTTY is found on bands whose band plan has no RTTY segment.
const RTTY_SPOTS = {
    '160m': 1_838_000, '80m': 3_580_000, '40m': 7_040_000, '30m': 10_140_000, '20m': 14_080_000,
    '17m': 18_105_000, '15m': 21_080_000, '12m': 24_925_000, '10m': 28_080_000, '6m': 50_300_000,
};

// The modes MODE offers: IWC's names, and what the radio's screen says, as
// on the Radio panel (MODE_LABEL in radio-panel-icom.js).
const RTTY_MODES = { 'RTTY-L': 'RTTY', 'RTTY-U': 'RTTY-R', 'DATA-L': 'LSB-D', 'DATA-U': 'USB-D' };

const num = v => { const n = Number(v); return Number.isFinite(n) ? n : 0; };
const on = v => v === true || v === 'true' || v === 1 || (typeof v === 'string' && v !== '' && v !== '0' && v !== '00' && v !== 'false');
const clamp = x => Math.max(0, Math.min(1, x));
// The IC-7300's widths for a mode (ic7300-if-width.js, loaded by the page).
const widthsForMode = mode => window.IfWidth?.widthsForMode?.(mode) ?? [];
const widthLabel = hz => (hz < 1000 ? hz + ' Hz' : (hz / 1000).toFixed(hz % 100 ? 2 : 1) + ' kHz');

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, pseudoDual, maxPowerW, minHz, maxHz, bandPlan, rttyMode, views, tuner, decode, ... }
 *        rttyMode: the operator's RTTY mode, from Settings
 */
export function startIcomRttyContest(root, cfg) {
    const maxPowerW = num(cfg.maxPowerW) || 100;
    let rttyMode = RTTY_MODES[cfg.rttyMode] ? cfg.rttyMode : 'RTTY-L';
    const receivers = [{ id: 'A', tab: 'VFO-A', name: 'VFO-A · SCOPE' }, { id: 'B', tab: 'VFO-B', name: 'VFO-B · SCOPE' }];
    const bandPlan = cfg.bandPlan || 'Region1';
    // One row of band keys, for the VFO on screen.
    const ROW = 'A';

    const view = mountContestView(root, {
        receivers,
        bands: BANDS.map(b => ({ id: b, label: b })),
        bandRows: [{ id: ROW, label: 'BAND' }],
        onBand: (_row, band) => toBand(view.receiver, band),
        onNudge: (vfo, hz) => stepper.nudge(vfo, hz),
        onReceiver: vfo => { if (vfo !== feed.state.sel) post(`/api/cat/active-vfo/${vfo}`); },
        spans: SPANS_HZ.map(hz => ({ hz, label: spanText(hz) })),
        onSpan: (id, hz) => feed.setSpan(id, hz),
        onTune: (vfo, hz) => feed.tune(vfo, hz),
        tuneTo: (vfo, hz) => feed.tuneTo(vfo, hz),
        onStep: (vfo, dir) => stepper.turn(vfo, dir),
        onFrequencyEntry: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
        modes: Object.entries(RTTY_MODES).map(([id, label]) => ({ id, label })),
        onMode: (vfo, mode) => chooseMode(vfo, mode),
        modeTitle: 'The mode of the VFO on screen. What you pick is also kept, in Settings, as the mode the band keys and the RTTY tuner use',
        controls: [
            { id: 'power', label: 'PWR', kind: 'range', min: 5, max: maxPowerW, step: 1, unit: ' W',
              title: 'RF power. Drag and let go, or turn the mouse wheel over it' },
            { id: 'width', label: 'WIDTH', kind: 'list', perReceiver: true,
              title: 'IF filter width of the VFO on screen',
              auto: 'The RTTY tuner works the width out for the signal. A width you set on VFO A while the tuner is open is kept for the mode' },
        ],
        onControl: (id, value, vfo) => setControl(id, value, vfo),
        meters: [{ id: 'S', label: 'S' }, { id: 'PO', label: 'PO' }, { id: 'SWR', label: 'SWR' }, { id: 'ALC', label: 'ALC' }],
        views: cfg.views ?? [],
        menu: cfg.menu ?? [],
        tuner: cfg.tuner,
        decode: cfg.decode,
        storagePrefix: 'iwc.rttyContest.',
        themePrefix: 'iwc.radioPanel.',
        fontBase: '/lib/fonts',
        minHz: cfg.minHz,
        maxHz: cfg.maxHz,
        part: cfg.part,
        onDock: cfg.onDock,
        windowUrl: cfg.windowUrl,
        onTunerOut: cfg.onTunerOut,
        onDecodeOut: cfg.onDecodeOut,
        layoutsApi: cfg.layoutsApi,
        onDecode: cfg.onDecode,
    });

    // A click is the middle of the pair. In RTTY the dial goes on the higher
    // tone, half the shift up; in DATA on the carrier, the pair's audio
    // midpoint away. Never a mode change.
    function clickTarget(_vfo, hz, mode) {
        const s = loadRttySettings();
        const half = num(s.shiftHz) / 2;
        const mid = afskMidpointAudioHz(s);
        const off = { 'RTTY-L': half, 'RTTY-U': half, 'DATA-L': mid, 'DATA-U': -mid }[mode] ?? 0;
        return { mode: null, to: Math.round(hz + off) };
    }
    const feed = startIcomSpectrumFeed({
        conn: cfg.conn,
        pseudoDual: cfg.pseudoDual,
        pane: vfo => view.pane(vfo),
        holds: vfo => stepper.holds(vfo),
        target: clickTarget,
        onChange: p => { if (p === 'ActiveVfo') showReceiver(); },
    });
    const stepper = dialStepper({
        // The page's own STEP, 1 Hz to 1 kHz, not the board's.
        stepHz: () => view.stepHz,
        freq: vfo => feed.state.freq[vfo],
        show: (vfo, hz) => { feed.showFrequency(vfo, hz); view.setFrequency(vfo, hz); },
        write: (vfo, hz) => post(`/api/cat/frequency/${vfo.toLowerCase()}`, { frequencyHz: hz }),
        maxHz: num(cfg.maxHz) || 74_800_000,
    });

    // The view follows the radio's selected VFO, however it was selected.
    function showReceiver() {
        if (view.receiver !== feed.state.sel) view.setReceiver(feed.state.sel);
        showBand();
        drawS();
    }

    // A band key: to the band's RTTY spot, or the radio's band memory, then
    // the RTTY mode.
    async function toBand(vfo, band) {
        const rx = vfo.toLowerCase();
        const spot = getSegments(bandPlan, band)?.RTTY?.freq ?? RTTY_SPOTS[band];
        const res = spot
            ? await post(`/api/cat/frequency/${rx}`, { frequencyHz: spot })
            : await post(`/api/cat/band/${rx}`, { band });
        if (res?.ok) await post(`/api/cat/mode/${rx}`, { mode: rttyMode });
    }
    // MODE: kept first, so a running tuner holds the new mode rather than
    // putting the old one back, then written to the VFO on screen.
    async function chooseMode(vfo, mode) {
        if (!RTTY_MODES[mode]) return;
        rttyMode = mode;
        const res = await post('/api/rtty/mode', { mode });
        if (res?.ok) await post(`/api/cat/mode/${vfo.toLowerCase()}`, { mode });
    }
    // PWR and WIDTH. Power has no VFO; the URL names the one on screen, as
    // the Radio panel's knob names the selected one.
    function setControl(id, value, vfo) {
        const rx = (vfo ?? view.receiver).toLowerCase();
        if (id === 'power') return post(`/api/cat/power/${rx}`, { Watts: Math.round(num(value)) });
        if (id === 'width' && value === 'auto') return widthToAuto();
        if (id === 'width') return post(`/api/radio/ifwidth/${rx}`, { hz: parseInt(value, 10) }).then(r => { checkKept(); return r; });
    }
    // WIDTH's choices change with the mode: in FM there are none.
    const widthOf = { A: null, B: null }, modeOf = { A: '', B: '' };
    function showWidth(vfo) {
        const opts = widthsForMode(modeOf[vfo]) ?? [];
        view.setControl('width', widthOf[vfo] == null ? null : String(widthOf[vfo]),
            { vfo, options: opts.map(hz => ({ id: String(hz), label: widthLabel(hz) })),
              kept: vfo === 'A' ? keptA : false });
    }
    // WIDTH's AUTO, VFO A only (the one the tuner sets): lit while the tuner
    // works the width out, dark while a width set by hand is kept for the
    // mode. Pressing it forgets the kept width and the tuner sizes the filter
    // again.
    let keptA = null, keptTimer = 0;
    function checkKept() {
        clearTimeout(keptTimer);
        keptTimer = setTimeout(async () => {
            try {
                const r = await fetch('/api/rtty/tuner/width');
                if (!r.ok) return;
                const { mode, hz } = await r.json();
                keptA = hz == null ? null
                    : `WIDTH ${widthLabel(hz)} is kept for ${mode}, as you set it. Press AUTO for the tuner to work the width out again`;
                showWidth('A');
            } catch { /* the key stays as it was */ }
        }, 300);
    }
    async function widthToAuto() {
        try {
            const r = await fetch('/api/rtty/tuner/width', { method: 'DELETE' });
            const body = await r.json().catch(() => null);
            if (body?.message) console.info('rtty contest:', body.message);
        } catch (e) { console.warn('rtty contest: WIDTH AUTO', e); }
        checkKept();
    }
    checkKept();

    // Which band key is lit follows the frequency of the VFO on screen.
    const bandAt = hz => (BAND_EDGES[bandPlan] ?? BAND_EDGES.Region1).find(e => hz >= e.lo && hz <= e.hi)?.name ?? null;
    const showBand = () => view.setBand(ROW, bandAt(feed.state.freq[view.receiver]));

    // The tuner keeps Mark and Shift in this browser's storage, which is
    // what a click reads too. The tuner changes them without an event this
    // page can hear - Auto, or the radio's own menu - so the strip looks
    // twice a second.
    let tones = '';
    function showTones() {
        const s = loadRttySettings();
        const t = `${s.markHz}/${s.shiftHz}`;
        if (t === tones) return;
        tones = t;
        view.setTones(num(s.markHz), num(s.shiftHz));
    }
    showTones();
    setInterval(showTones, 500);

    // The meters. S is the radio's one receiver, so the VFO it is on; PO,
    // SWR and ALC read only while transmitting, averaged as on YWC's page.
    const tx = { on: false, po: [], swr: [], alc: 0 };
    const avg = a => (a.length ? a.reduce((s, x) => s + x, 0) / a.length : 0);
    let sValue = 0;
    function drawS() {
        const v = tx.on || view.receiver !== feed.state.sel ? 0 : sValue;
        view.setMeter('S', sMeterFraction(v), sMeterText(v));
    }
    function drawTx() {
        const po = tx.on ? Math.min(maxPowerW, calibrateNumeric('PWR', avg(tx.po))) : 0;
        const swr = tx.on && tx.swr.length ? calibrateNumeric('SWR', avg(tx.swr)) : 0;
        const alc = tx.on ? calibrateNumeric('ALC', num(tx.alc)) : 0;
        view.setMeter('PO', clamp(po / maxPowerW), tx.on ? `${Math.round(po)} W` : '');
        view.setMeter('SWR', txMeterFraction('SWR', swr), swr > 0 ? swr.toFixed(1) : '', swr >= 3);
        view.setMeter('ALC', clamp(alc / 100), '');
    }
    const keep = (a, raw, n) => { a.push(num(raw)); if (a.length > n) a.shift(); };

    const handlers = {
        FrequencyA: v => { if (view.receiver === 'A') showBand(); if (!stepper.holds('A')) view.setFrequency('A', num(v)); },
        FrequencyB: v => { if (view.receiver === 'B') showBand(); if (!stepper.holds('B')) view.setFrequency('B', num(v)); },
        ModeA: v => { modeOf.A = String(v ?? ''); view.setMode('A', modeOf.A); showWidth('A'); checkKept(); },
        ModeB: v => { modeOf.B = String(v ?? ''); view.setMode('B', modeOf.B); showWidth('B'); },
        IfWidthA: v => { widthOf.A = v === '' || v == null ? null : num(v); showWidth('A'); },
        IfWidthB: v => { widthOf.B = v === '' || v == null ? null : num(v); showWidth('B'); },
        Power: v => view.setControl('power', num(v)),
        SMeterA: v => { sValue = calibrateNumeric('SMETER', num(v)); drawS(); },
        IsTransmitting: v => {
            tx.on = on(v);
            if (!tx.on) { tx.po = []; tx.swr = []; tx.alc = 0; }
            view.setTx(tx.on);
            drawS();
            drawTx();
        },
        PowerMeter: v => { if (!tx.on) return; keep(tx.po, typeof v === 'object' && v ? v.value : v, 4); drawTx(); },
        // Under 1 W the SWR reading measures nothing (the #124 trap): leave it out.
        SWRMeter: v => { if (!tx.on || calibrateNumeric('PWR', avg(tx.po)) < 1 || !num(v)) return; keep(tx.swr, v, 3); drawTx(); },
        ALCMeter: v => { if (!tx.on) return; tx.alc = v; drawTx(); },
    };
    cfg.conn.on('RadioStateUpdate', msg => {
        const fn = handlers[msg?.property];
        if (fn) try { fn(msg.value); } catch (e) { console.warn('rtty contest:', msg.property, e); }
    });

    loadFromBackend({ 'S-Meter': ['SMETER', 'SMETER_LABELS'] });
    drawS();
    drawTx();
    cfg.conn.start().catch(e => console.warn('rtty contest: hub', e));
    // Only the VFO on screen is showing, so the server never peeks the other.
    reportSpectrumPanes(cfg.conn, () => ({ A: view.receiver === 'A', B: view.receiver === 'B' }));
    return view;
}
