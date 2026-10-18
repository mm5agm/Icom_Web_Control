// radio-panel-icom-windows.js - which windows IWC's radio panel offers, and
// what goes in them.
//
// The window frame (launcher keys, drag, close, tear-off, remembered places)
// is shared: core/js/radio-panel/panel-windows.js. So are the framed pages,
// the ⇱ browser windows, memories, the DX watch list and the keypad
// (core/js/radio-panel/window-kit.js). This file is the Icom side: the list of
// windows, and the panels that read and set the radio.
//
// Positions are [left, top, width, height] in the panel's own 1600-wide board
// pixels, so they are the same at every zoom.
//
// The CW reader and send, the RTTY tuner/reader, the DX spots and the Twin
// PBT page are the pages Classic already pops out, shown here in a frame.
// Those pages know when they are framed (core popout.js) and do not announce
// themselves as pop-outs. A framed page is loaded the first time its panel is
// shown. The decoders run in the server, so only the text on screen starts
// again when a frame is moved.
//
// Not here, as YWC has them: Remote Audio and the radio's own scope page (IWC
// has neither page), the radio screen (video is YWC-only), and TX SETUP - VOX
// and TUNE key the radio, and Classic keeps them.
//
// What transmits: CW send's Send and keyer memories, which say so in red on
// that page.

import { calibrateNumeric } from '/js/calibration/calibration-engine.js';
import { sMeterFraction, sMeterText, txMeterFraction } from '/js/radio-panel/panel-math.js';
import { SMeterHistoryPanel } from '/js/guages/smeter-history-panel.js';
import {
    num, h, note, row, keys, framed, windowPopouts, openPopout, meterBars,
    memoriesWindow, dxWatchWindow, frequencyEntryWindow, wheelSteps,
} from '/js/radio-panel/window-kit.js';

export const ICOM_WINDOWS = [
    { id: 'cw', key: 'CW', title: 'CW', tip: 'Open or close the CW window: keyer, reader and keyboard send. Opening it switches the radio to CW', pos: [330, 230, 980, 340], popout: { width: 1000, height: 400 }, panels: [
        { id: 'keyer', title: 'KEYER', w: 230 },
        { id: 'cwReader', title: 'READER', dpos: [380, 560, 760, 320],
          popout: { name: 'cw-reader', url: '/CwReader' } },
        { id: 'cwSend', title: 'SEND', w: 320, dpos: [420, 300, 520, 400],
          popout: { name: 'cw-send', url: '/CwSend', size: { width: 600, height: 340 } } },
    ] },
    { id: 'rtty', key: 'RTTY', title: 'RTTY', tip: 'Open or close the RTTY window: reader and tuner. Opening it switches the radio to RTTY', pos: [330, 380, 1040, 420], popout: { width: 1060, height: 480 }, panels: [
        { id: 'rttyReader', title: 'TUNER · READER', dpos: [360, 380, 820, 420],
          popout: { name: 'rtty-tuner', url: '/RttyTuner', size: { width: 380, height: 520 } } },
        { id: 'rttySend', title: 'SEND', w: 220 },
    ] },
    { id: 'mem', key: 'MEMORIES', title: 'MEMORIES', tip: "Open or close IWC's memories and banks", pos: [560, 150, 940, 460], popout: { width: 960, height: 500 }, panels: [
        { id: 'memBank', title: 'BANK', w: 200 },
        { id: 'memTiles', title: 'CHANNELS' },
        { id: 'memRadio', title: 'SAVE', w: 170 },
    ] },
    { id: 'dx', key: 'DX', title: 'DX CLUSTER', tip: 'Open or close the DX cluster spots', pos: [700, 150, 860, 480], panels: [
        { id: 'dxSpots', title: 'SPOTS',
          popout: { name: 'dx-spots', url: '/DxSpots', size: { width: 760, height: 480 } } },
        { id: 'dxWatch', title: 'WATCH LIST', w: 220 },
    ] },
    { id: 'meters', key: 'METERS', title: 'METERS', tip: 'Open or close the full meter set', pos: [330, 380, 1000, 400], popout: { width: 1020, height: 440 }, panels: [
        { id: 'mGauges', title: 'ALL METERS', w: 380 },
        { id: 'mHistory', title: 'S-METER HISTORY' },
    ] },
    { id: 'rx', key: 'RX SETUP', title: 'RX SETUP', tip: 'Open or close the receive settings', pos: [240, 180, 1000, 470], popout: { width: 1020, height: 510 }, panels: [
        { id: 'rxFilter', title: 'TWIN PBT', dpos: [280, 200, 560, 360] },
        { id: 'rxLevels', title: 'LEVELS', w: 240 },
    ] },
    // No launcher key: the ENT chip in a VFO box opens it.
    { id: 'kbd', title: 'FREQUENCY ENTRY', pos: [600, 200, 360, 470], panels: [
        { id: 'kbdPad', title: 'KEYPAD' },
    ] },
];

// The pop-out pages' own window controls mean nothing in a frame: the panel
// window has its own title, close and tear-off.
const EMBED_CSS = `
[id$="ReattachBtn"], .iwc-dialog-close, .popout-screen-offer,
[id^="audioFilterReattachBtn"], [id^="audioFilterPopoutBtn"],
#cwReaderTitle, #cwSendTitle, #rttyTunerTitle, #dxSpotsTitle { display: none !important; }
html, body { background: #111 !important; }
`;

/**
 * Fill the window panels. Each panel's body is handed out once and kept for
 * the page's life.
 * @param {object} windows  the panel's windows API (panel-windows.js)
 * @param {object} ctx      { maxPowerW, maxHz, post(url, body), selected() -> 'A'|'B', freq(vfo),
 *                            popouts - false in a window group's own page (/RadioWindow), which opens none }
 * @returns {{ onShow(g, p, shown), onRadioState(msg), entry(vfo), popout(g, p) }}
 */
export function startIcomWindows(windows, ctx) {
    const shows = {};       // 'g/p' -> fn(shown)
    const kit = { windows, shows };
    const slot = (g, p) => windows.slot(g, p);
    const state = {};       // hub property -> last value
    const embed = { embedCss: EMBED_CSS };

    framed(kit, 'cw', 'cwReader', '/CwReader', 'CW reader', embed);
    framed(kit, 'cw', 'cwSend', '/CwSend', 'CW send', embed);
    framed(kit, 'rtty', 'rttyReader', '/RttyTuner', 'RTTY tuner and reader', embed);
    framed(kit, 'dx', 'dxSpots', '/DxSpots', 'DX spots', embed);
    const filterFrame = framed(kit, 'rx', 'rxFilter', () => '/AudioFilter?vfo=' + ctx.selected(), 'Twin PBT', embed);

    const popouts = ctx.popouts === false ? {} : windowPopouts(kit, ICOM_WINDOWS);

    // ---- CW keyer --------------------------------------------------------
    const keyer = (() => {
        const body = slot('cw', 'keyer');
        const speed = row(body, 'SPEED');
        keys(body, [['−', () => setSpeed(-1)], ['+', () => setSpeed(1)]]);
        const pitch = row(body, 'PITCH');
        keys(body, [['−', () => setPitch(-10)], ['+', () => setPitch(10)]]);
        const bk = row(body, 'BREAK-IN');
        // The mouse wheel over SPEED or PITCH steps it, as the - / + keys do.
        wheelSteps(speed.parentElement, d => setSpeed(d));
        wheelSteps(pitch.parentElement, d => setPitch(d * 10));
        // Classic's ZIN: nudges the VFO so the signal the CW reader hears sits
        // on the pitch. It tunes; it does not transmit. It needs the reader
        // running, and says so if it is not.
        keys(body, [['ZERO IN', () => zeroIn()]]);
        const msg = h('div', 'rp-wnote', '');
        body.append(msg);
        function setSpeed(d) {
            const wpm = Math.max(6, Math.min(48, num(state.CwSpeed) + d));
            if (!state.CwSpeed || wpm === num(state.CwSpeed)) return;
            state.CwSpeed = wpm;
            ctx.post('/api/cat/cw/speed', { speed: wpm });
        }
        function setPitch(d) {
            const hz = Math.max(300, Math.min(900, num(state.CwPitch) + d));
            if (state.CwPitch == null || hz === num(state.CwPitch)) return;
            state.CwPitch = hz;
            ctx.post('/api/cat/cwpitch', { hz });
        }
        async function zeroIn() {
            const res = await ctx.post('/api/cw/zin');
            let j = null;
            try { j = res?.ok ? await res.json() : null; } catch { /* no body */ }
            msg.textContent = j?.message ?? (res?.ok ? '' : 'Zero-in failed.');
        }
        return {
            CwSpeed: v => { speed.textContent = num(v) + ' wpm'; },
            CwPitch: v => { pitch.textContent = num(v) + ' Hz'; },
            CwBreakIn: v => { bk.textContent = { 0: 'OFF', 1: 'SEMI', 2: 'FULL' }[String(v)] ?? String(v); },
        };
    })();

    // ---- meters ----------------------------------------------------------
    // The IC-7300's own scales, as on the board: 100 W, Id to 25 A, Vd 10-16 V.
    const meters = (() => {
        const bars = meterBars(slot('meters', 'mGauges'));
        bars.add('SA', 'S');
        bars.add('PO', 'PO', true); bars.add('SWR', 'SWR', true); bars.add('ALC', 'ALC', true);
        bars.add('COMP', 'COMP', true); bars.add('IDD', 'IDD', true);
        bars.add('VDD', 'VDD');

        let history = null;
        const histBody = slot('meters', 'mHistory');
        const canvas = h('canvas', 'rp-wcanvas');
        canvas.id = 'rpWinSMeterHistory';
        histBody.append(canvas);
        note(histBody, 'Last 60 s');
        shows['meters/mHistory'] = shown => {
            // The history panel finds its canvas by id, so it is made once the
            // canvas is in the document.
            if (shown && !history) history = new SMeterHistoryPanel(canvas.id, { windowMs: 60000 });
        };
        const raw = v => num(typeof v === 'object' && v ? v.value : v);
        const tx = (id, cal, frac, fmt) => v => { const x = cal(raw(v)); bars.set(id, frac(x), fmt(x)); };
        return {
            SMeterA: v => {
                const s = calibrateNumeric('SMETER', num(v));
                bars.set('SA', sMeterFraction(s), sMeterText(s));
                history?.push(num(v));
            },
            PowerMeter: tx('PO', r => Math.min(ctx.maxPowerW, calibrateNumeric('PWR', r)), x => x / ctx.maxPowerW, x => Math.round(x) + ' W'),
            SWRMeter: tx('SWR', r => calibrateNumeric('SWR', r), x => txMeterFraction('SWR', x), x => (x > 0 ? x.toFixed(1) : '--')),
            ALCMeter: tx('ALC', r => calibrateNumeric('ALC', r), x => x / 100, x => Math.round(x) + ' %'),
            CompressionMeter: tx('COMP', r => Math.max(0, Math.min(20, calibrateNumeric('Compression', r))), x => txMeterFraction('COMP', x), x => x.toFixed(0) + ' dB'),
            IDDMeter: tx('IDD', r => calibrateNumeric('IDD', r), x => x / 25, x => x.toFixed(1) + ' A'),
            // Raw 0 is no reading; a Vd off the 10-16 V dial is a bad read.
            VDDMeter: v => {
                if (num(v) <= 0) return;
                const x = calibrateNumeric('VPA', num(v));
                if (x >= 10 && x <= 16) bars.set('VDD', txMeterFraction('VDD', x, { min: 10, max: 16 }), x.toFixed(1) + ' V');
            },
        };
    })();

    const memories = memoriesWindow(kit, { post: ctx.post, selected: ctx.selected, freq: ctx.freq, dual: false, appShort: 'IWC' });
    dxWatchWindow(kit);

    // ---- RX setup: squelch, NB and NR levels -------------------------------
    // The radio's, not a VFO's: the IC-7300 has one receiver. Each value
    // changes as soon as a key is pressed and is put right by the radio's own
    // value when it comes back over the hub. Nothing is sent until the radio
    // has reported the value, so a step never starts from a guess.
    const levels = (() => {
        const body = slot('rx', 'rxLevels');
        const val = { SquelchA: null, NbLevelA: null, NrLevelA: null };
        const pct = x => (x == null ? '--' : Math.round(x / 2.55) + '%');
        const sqlOut = row(body, 'SQUELCH');
        keys(body, [['−10', () => step('SquelchA', -26, 'squelch', 'value')], ['−1', () => step('SquelchA', -3, 'squelch', 'value')],
            ['+1', () => step('SquelchA', 3, 'squelch', 'value')], ['+10', () => step('SquelchA', 26, 'squelch', 'value')]]);
        const nbOut = row(body, 'NB LEVEL');
        keys(body, [['−10', () => step('NbLevelA', -26, 'nblevel', 'level')], ['+10', () => step('NbLevelA', 26, 'nblevel', 'level')]]);
        const nrOut = row(body, 'NR LEVEL');
        keys(body, [['−10', () => step('NrLevelA', -26, 'nrlevel', 'level')], ['+10', () => step('NrLevelA', 26, 'nrlevel', 'level')]]);
        note(body, 'Steps are in % of the radio\'s 0-255 range.');
        function step(k, d, path, field) {
            if (val[k] == null) return;     // not read from the radio yet
            const to = Math.max(0, Math.min(255, val[k] + d));
            if (to === val[k]) return;
            val[k] = to;
            ctx.post(`/api/cat/${path}/${ctx.selected().toLowerCase()}`, { [field]: to });
            draw();
        }
        function draw() {
            sqlOut.textContent = pct(val.SquelchA);
            nbOut.textContent = pct(val.NbLevelA);
            nrOut.textContent = pct(val.NrLevelA);
        }
        draw();
        const out = {};
        for (const k of Object.keys(val)) out[k] = x => { val[k] = num(x); draw(); };
        return out;
    })();

    const entry = frequencyEntryWindow(kit, { post: ctx.post, freq: ctx.freq, dual: false, maxHz: ctx.maxHz });

    // The IC-7300's CI-V has no command that sends RTTY text (17 is CW only).
    const rttySend = slot('rtty', 'rttySend');
    if (rttySend) note(rttySend, "Not built. The radio has no CI-V command that sends RTTY text. For now, send RTTY from your RTTY program, or from the radio's own RTTY memories.");

    // Windows left open last time were shown while the panel was mounting,
    // before any of the above existed.
    for (const g of ICOM_WINDOWS)
        for (const p of g.panels)
            if (windows.isShown(g.id, p.id)) shows[g.id + '/' + p.id]?.(true);

    // The Twin PBT page follows the selected VFO.
    const rxFollow = () => {
        windows.setSubtitle('rx', 'VFO-' + ctx.selected());
        filterFrame?.reload();
    };
    rxFollow();

    const handlers = [keyer, meters, levels];
    return {
        onShow(g, p, shown) { try { shows[g + '/' + p]?.(shown); } catch (e) { console.warn('radio panel window:', g, p, e); } },
        onRadioState(msg) {
            const prop = msg?.property;
            if (!prop) return;
            state[prop] = msg.value;
            for (const hset of handlers) {
                const fn = hset[prop];
                if (fn) try { fn(msg.value); } catch (e) { console.warn('radio panel window:', prop, e); }
            }
            if (prop === 'FrequencyA' || prop === 'FrequencyB' || prop === 'ActiveVfo') { memories.mark(); entry.draw(); }
            if (prop === 'ActiveVfo') rxFollow();
        },
        entry: vfo => entry.open(vfo),
        popout: (g, p) => openPopout(popouts, g, p),
    };
}
