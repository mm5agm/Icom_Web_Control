// Icom Web Control - browser checks for the pages and pop-outs
//
//   node Tests/ui/iwc-ui.mjs [--base http://localhost:8080] [--headed] [--only text]
//
// The app must already be running. Nothing here reaches the radio: every
// write is stubbed and recorded, and the mode / frequency the pages see comes
// from the test, not from the radio (see core/tests/ui/browser-harness.mjs).
// So it is safe to run with the radio connected, but it does not prove
// anything about CAT itself - only that the pages do the right thing with
// what the radio reports.

import { Browser, Suite, expect, sleep } from '../../core/tests/ui/browser-harness.mjs';

const args = process.argv.slice(2);
const arg = (name, fallback) => { const i = args.indexOf(name); return i >= 0 ? args[i + 1] : fallback; };
const BASE = arg('--base', 'http://localhost:8080').replace(/\/$/, '');
const ONLY = arg('--only', '');
const SETTLE = 2000 + 700; // core's MODE_SETTLE_MS, plus margin

const PAGES = [
    '/', '/About', '/ApplicationSetup', '/AudioFilter', '/Calibrations',
    '/Calibration/MeterCalibration',
    '/CwReader', '/CwSend', '/Diagnostics', '/DxSpots', '/Labels', '/Memories',
    '/Ports', '/RttyTuner', '/Settings', '/UserManual', '/radio', '/flexui',
    '/RadioSpectrum', '/RadioWindow?g=cw', '/RadioWindow?g=rtty', '/RadioWindow?g=mem',
    '/RadioWindow?g=meters', '/RadioWindow?g=rx',
];

const suite = new Suite(`Icom Web Control UI checks against ${BASE}`);
const test = (name, fn) => { if (!ONLY || name.toLowerCase().includes(ONLY.toLowerCase())) suite.test(name, fn); };

let browser;
const open = (path, opts) => browser.open(BASE + path, opts);
const modeA = (page, mode) => page.state('ModeA', mode);
const visible = (page, sel) => page.eval(`(() => { const e = document.querySelector(${JSON.stringify(sel)}); return !!e && !e.hidden && e.getClientRects().length > 0; })()`);
const text = (page, sel) => page.eval(`document.querySelector(${JSON.stringify(sel)})?.textContent ?? null`);

// Same-origin requests a page made that failed, minus ones that are about the
// radio's state rather than the page (a disconnected radio 5xx's some reads).
// KNOWN are faults that predate the pop-out work, reported as notes so a new
// one still fails. Remove a line when its fault is fixed.
const KNOWN = [
    // e.g. /^404 \/some\/old\.js/,   // why, and where it is tracked
];
const pageFaults = page => page.failed.filter(f => !/^5\d\d \/api\//.test(f) && !KNOWN.some(k => k.test(f)));
const knownFaults = page => [...new Set(page.failed.filter(f => KNOWN.some(k => k.test(f))))];

// ---------------------------------------------------------------- preflight

test('build serves the core modules and not the deleted local copies', async () => {
    const status = async p => (await fetch(BASE + p)).status;
    for (const p of ['/js/hub/hub-connection.js', '/js/dx/dx-spots-panel.js', '/js/popout/popout-mode-pause.js', '/js/modes/mode-panel-guard.js']) {
        expect(await status(p) === 200, `${p} is not served - is the app built from this branch?`);
    }
    for (const p of ['/js/ui/hub-connection.js', '/js/ui/dx-spots-panel.js']) {
        expect(await status(p) === 404, `${p} is still served - the app is running an older build`);
    }
});

// ---------------------------------------------------------------- every page

for (const path of PAGES) {
    test(`page loads cleanly: ${path}`, async note => {
        const page = await open(path, { settleMs: 2500 });
        try {
            const faults = pageFaults(page);
            for (const k of knownFaults(page)) note(`known, older than this branch: ${k}`);
            expect(faults.length === 0, `failed requests:\n${faults.join('\n')}`);
            expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
            const hub = await page.eval('({ started: __rwcTest.started, errors: __rwcTest.startErrors, handlers: __rwcTest.handlers.length })');
            expect(hub.errors.length === 0, `hub failed to start: ${hub.errors.join('; ')}`);
            if (hub.handlers && !hub.started) note('registered hub handlers but the hub had not connected after 2.5 s');
            const writes = await page.writes();
            if (writes.length) note(`writes on load (stubbed): ${writes.map(w => `${w.method} ${w.path}`).join(', ')}`);
            if (page.consoleErrors.length) note(`console errors: ${page.consoleErrors.slice(0, 3).join(' | ')}`);
        } finally { await page.close(); }
    });
}

test('Calibrations page shows the tables, not its loading text', async () => {
    const page = await open('/Calibrations');
    try {
        await page.waitFor(`!document.getElementById('calibration-app').textContent.includes('Loading')`,
            { timeoutMs: 5000, what: 'calibration-editor.js to replace the loading text' });
        const t = await text(page, '#calibration-app');
        expect(!t.includes('Failed'), `the page says: ${t.slice(0, 120)}`);
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- pop-outs

async function modePopoutChecks({ path, pausedSel, announceSel, name, good, bad, note }) {
    const page = await open(path);
    try {
        await modeA(page, good);
        await page.waitFor(`document.querySelector(${JSON.stringify(pausedSel)}).hidden`, { what: `${name} running in ${good}` });

        // A blip out and straight back never pauses it.
        await modeA(page, bad);
        await sleep(400);
        await modeA(page, good);
        await sleep(SETTLE);
        expect(!(await visible(page, pausedSel)), `${name} paused after a 400 ms blip to ${bad}`);

        // Settled in the wrong mode: pauses, says why, and says so aloud.
        await page.clearWrites();
        await modeA(page, bad);
        await sleep(1000);
        expect(!(await visible(page, pausedSel)), `${name} paused before the settle time`);
        await sleep(SETTLE - 1000);
        expect(await visible(page, pausedSel), `${name} did not pause after ${bad} settled`);
        const why = await text(page, pausedSel);
        expect(why.includes(bad), `paused text does not name ${bad}: "${why}"`);
        await page.waitFor(`document.querySelector(${JSON.stringify(announceSel)}).textContent === ${JSON.stringify(`${name} paused, mode is now ${bad}`)}`,
            { timeoutMs: 2000, what: 'pause announcement' });
        const onPause = await page.writes();

        // Back in its mode: running again at once, no settle wait.
        await page.clearWrites();
        await modeA(page, good);
        await sleep(300);
        expect(!(await visible(page, pausedSel)), `${name} did not resume at once on ${good}`);
        await page.waitFor(`document.querySelector(${JSON.stringify(announceSel)}).textContent === ${JSON.stringify(`${name} running again`)}`,
            { timeoutMs: 2000, what: 'resume announcement' });
        const onResume = await page.writes();

        expect(page.exceptions.length === 0, `exceptions: ${page.exceptions.join('; ')}`);
        return { onPause, onResume };
    } finally { await page.close(); }
}

test('CW Reader pop-out pauses outside CW and resumes in CW', async note => {
    await modePopoutChecks({ path: '/CwReader', pausedSel: '#cwReaderPaused', announceSel: '#cwReaderAnnounce',
        name: 'CW reader', good: 'CW-U', bad: 'USB', note });
});

test('RTTY Tuner pop-out pauses outside RTTY/DATA/SSB and resumes', async note => {
    const { onPause, onResume } = await modePopoutChecks({ path: '/RttyTuner', pausedSel: '#rttyTunerPaused', announceSel: '#rttyTunerAnnounce',
        name: 'RTTY panel', good: 'RTTY-L', bad: 'CW-U', note });
    expect(onPause.some(w => /\/api\/rtty\/tuner\/stop/.test(w.path)), `no tuner stop sent on pause (sent: ${onPause.map(w => w.path).join(', ') || 'nothing'})`);
    expect(onResume.some(w => /\/api\/rtty\/tuner\/start/.test(w.path)), `no tuner start sent on resume (sent: ${onResume.map(w => w.path).join(', ') || 'nothing'})`);
});

test('RTTY Tuner pop-out stays running in every mode it belongs to', async () => {
    const page = await open('/RttyTuner');
    try {
        for (const m of ['RTTY-L', 'RTTY-U', 'DATA-L', 'DATA-U', 'PSK', 'LSB', 'USB']) {
            await modeA(page, m);
        }
        await sleep(SETTLE);
        expect(!(await visible(page, '#rttyTunerPaused')), 'paused in one of its own modes');
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- main page dialogs

test('Index: CW reader dialog closes when the mode settles outside CW, not on a blip', async () => {
    const page = await open('/', { settleMs: 2500 });
    try {
        await modeA(page, 'CW-U');
        await sleep(SETTLE);
        await page.eval(`document.getElementById('cwReaderDialog').open || window.cwReaderPanel.toggle()`);
        await page.waitFor(`document.getElementById('cwReaderDialog').open`, { what: 'CW reader dialog open' });

        await modeA(page, 'USB'); await sleep(400); await modeA(page, 'CW-U');
        await sleep(SETTLE);
        expect(await page.eval(`document.getElementById('cwReaderDialog').open`), 'closed on a 400 ms blip');

        await modeA(page, 'USB');
        await sleep(SETTLE);
        expect(!(await page.eval(`document.getElementById('cwReaderDialog').open`)), 'still open after USB settled');
    } finally { await page.close(); }
});

test('Index: RTTY tuner dialog closes when the mode settles in CW, stays in USB', async () => {
    const page = await open('/', { settleMs: 2500 });
    try {
        await modeA(page, 'RTTY-L');
        await sleep(SETTLE);
        await page.eval(`document.getElementById('rttyTunerDialog').open || window.rttyTuner.toggle()`);
        await page.waitFor(`document.getElementById('rttyTunerDialog').open`, { what: 'RTTY tuner dialog open' });

        await modeA(page, 'USB');
        await sleep(SETTLE);
        expect(await page.eval(`document.getElementById('rttyTunerDialog').open`), 'closed in USB, where it belongs');

        await modeA(page, 'CW-U');
        await sleep(SETTLE);
        expect(!(await page.eval(`document.getElementById('rttyTunerDialog').open`)), 'still open after CW settled');
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- DX spots

const SPOT_HZ = 14_025_000;    // CW in every region's band plan
const SPOT = { callsign: 'T3ST', frequencyHz: SPOT_HZ, spotter: 'MM5AGM', comment: 'ui test', receivedUtc: new Date().toISOString(), isWatched: false };
const freqWrites = ws => ws.filter(w => w.path.startsWith('/api/cat/frequency/a'));
const modeWrites = ws => ws.filter(w => w.path.startsWith('/api/cat/mode/a'));

async function indexWithSpot() {
    const page = await open('/', { settleMs: 2500, before: `try { localStorage.setItem('dxOnlyWatched', '0'); } catch {}` });
    await page.eval(`(() => {
        window.dxOnlyWatched = false;
        window.dxSpotsPanel.setVfoFrequency(14_200_000);
        window.dxSpotsPanel.addSpot(${JSON.stringify(SPOT)});
        window.dxSpotsPanel.show();
    })()`);
    await page.waitFor(`document.getElementById('dxSpotsDialog').open && document.querySelector('#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]')`, { what: 'test spot row' });
    // The start-up overlay covers the whole page, and on this app it waits up
    // to 12 s for the first scope sweep - well past the settle time - so a
    // click before it goes lands on the overlay, not the dialog.
    await page.waitFor(`getComputedStyle(document.getElementById('initOverlay')).display === 'none'`,
        { timeoutMs: 16000, what: 'start-up overlay gone' });
    return page;
}

test('Index: DX Spots opens on screen the first time, with no saved place', async () => {
    const page = await open('/', { settleMs: 2500, before: `try { localStorage.removeItem('dxSpotsPanel'); } catch {}` });
    try {
        await page.eval(`window.dxSpotsPanel.show()`);
        const r = await page.eval(`(() => { const r = document.getElementById('dxSpotsDialog').getBoundingClientRect();
            return { top: Math.round(r.top), bottom: Math.round(r.bottom), vh: innerHeight, page: document.documentElement.scrollHeight }; })()`);
        expect(r.top >= 0 && r.top < r.vh - 40,
            `the dialog's top is at ${r.top} px in a ${r.vh} px window (page ${r.page} px tall) - the operator sees nothing open`);
    } finally { await page.close(); }
});

for (const auto of [true, false]) {
    test(`Index: clicking a DX spot ${auto ? 'changes' : 'does not change'} mode with auto-mode ${auto ? 'ON' : 'OFF'}`, async () => {
        const page = await indexWithSpot();
        try {
            await page.eval(`window.iwcAutoModeChangeOnTune = ${auto}`);
            await page.clearWrites();
            await page.click(`#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]`);
            await sleep(500);
            const ws = await page.writes();
            const f = freqWrites(ws);
            expect(f.length === 1 && f[0].body.frequencyHz === SPOT_HZ, `expected one frequency write of ${SPOT_HZ}, got ${JSON.stringify(f)}`);
            const m = modeWrites(ws);
            if (auto) expect(m.length === 1 && m[0].body.mode === '3', `expected one mode write of CW-U ("3"), got ${JSON.stringify(m)}`);
            else expect(m.length === 0, `mode written with auto-mode off: ${JSON.stringify(m)}`);
        } finally { await page.close(); }
    });
}

for (const auto of [true, false]) {
    test(`DxSpots pop-out: clicking a spot ${auto ? 'changes' : 'does not change'} mode with auto-mode ${auto ? 'ON' : 'OFF'}`, async () => {
        const before = `
            try { localStorage.setItem('dxOnlyWatched', '0'); } catch {}
            __rwcTest.getOverrides['/api/dxcluster/spots'] = [${JSON.stringify(SPOT)}];
            __rwcTest.getOverrides['/DxSpots?handler=AutoMode'] = { autoModeChangeOnTune: ${auto} };`;
        const page = await open('/DxSpots', { before });
        try {
            await page.state('FrequencyA', 14_200_000);
            await page.waitFor(`document.querySelector('#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]')`, { what: 'test spot row' });
            await page.clearWrites();
            await page.click(`#dxSpotsTbody tr[data-hz="${SPOT_HZ}"]`);
            await sleep(800);
            const ws = await page.writes();
            const f = freqWrites(ws);
            expect(f.length === 1 && f[0].body.frequencyHz === SPOT_HZ, `expected one frequency write of ${SPOT_HZ}, got ${JSON.stringify(f)}`);
            const m = modeWrites(ws);
            if (auto) expect(m.length === 1 && m[0].body.mode === '3', `expected one mode write of CW-U ("3"), got ${JSON.stringify(m)}`);
            else expect(m.length === 0, `mode written with auto-mode off: ${JSON.stringify(m)}`);
        } finally { await page.close(); }
    });
}

test('DxSpots pop-out shows a spot pushed over the hub', async () => {
    const page = await open('/DxSpots', { before: `__rwcTest.getOverrides['/api/dxcluster/spots'] = [];` });
    try {
        await page.state('FrequencyA', 14_200_000);
        await page.state('DxSpot', { ...SPOT, callsign: 'T3ST2', frequencyHz: 14_030_000 });
        await page.waitFor(`document.querySelector('#dxSpotsTbody tr[data-hz="14030000"]')`, { timeoutMs: 3000, what: 'pushed spot row' });
    } finally { await page.close(); }
});

// The "set it up" pop-outs (core/js/dialogs/feature-setup.js). State is
// faked, so no test reads or writes the real settings.
const FS_STATE = {
    dxCluster: { enabled: false, host: '', port: 7300, callsign: '' },
    cwAudio: { rxDevice: 'Line (USB AUDIO  CODEC)', devices: [
        { key: 'Microphone (Webcam)', name: 'Microphone (Webcam)', likelyRadio: false },
        { key: 'Line (USB AUDIO  CODEC)', name: 'Line (USB AUDIO  CODEC)', likelyRadio: true },
    ] },
};
const fsOpen = path => open(path, { settleMs: 1500, before: `__rwcTest.getOverrides['/api/feature-setup/state'] = ${JSON.stringify(FS_STATE)};` });

for (const path of ['/', '/CwReader', '/DxSpots'])
    test(`${path}: provides the CW audio and DX cluster set-up pop-outs`, async () => {
        const page = await fsOpen(path);
        try {
            await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
            expect(await page.eval(`radioFeatureSetup.provides('cw-audio') && radioFeatureSetup.provides('dx-cluster')`), 'a pop-out is missing');
        } finally { await page.close(); }
    });

test('CW audio pop-out: shows the current device, saves only the one picked', async () => {
    const page = await fsOpen('/CwReader');
    try {
        await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
        await page.eval(`radioFeatureSetup.open('cw-audio')`);
        await page.waitFor(`document.getElementById('featureSetupDialog')?.open`, { what: 'the dialog' });
        const sel = await page.eval(`(() => { const s = document.getElementById('fsCwRx'); return { v: s.value, opts: [...s.options].map(o => o.textContent) }; })()`);
        expect(sel.v === 'Line (USB AUDIO  CODEC)', `selected "${sel.v}"`);
        expect(sel.opts.some(t => /CODEC\) \(looks like the radio\)/.test(t)), `options ${JSON.stringify(sel.opts)}`);
        await page.eval(`(() => { const s = document.getElementById('fsCwRx'); s.value = 'Microphone (Webcam)'; document.getElementById('featureSetupSave').click(); })()`);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the save' });
        // Only the pop-out's own writes: the main page may also send a
        // load-time voice nudge-step sync (tuning-step.js), which is not ours.
        const w = (await page.writes()).filter(x => x.path.startsWith('/api/feature-setup/'));
        expect(w.length === 1 && w[0].path === '/api/feature-setup/audio-rx' && w[0].body.device === 'Microphone (Webcam)', `wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
});

test('DX cluster pop-out: saves the four cluster fields', async () => {
    const page = await fsOpen('/');
    try {
        await page.waitFor(`!!window.radioFeatureSetup`, { timeoutMs: 8000, what: 'window.radioFeatureSetup' });
        await page.eval(`radioFeatureSetup.open('dx-cluster')`);
        await page.waitFor(`document.getElementById('featureSetupDialog')?.open`, { what: 'the dialog' });
        await page.eval(`(() => {
            document.getElementById('fsDxHost').value = 'dx.example.org';
            document.getElementById('fsDxCall').value = 'n0call';
            document.getElementById('featureSetupSave').click();
        })()`);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the save' });
        // Only the pop-out's own writes: the main page may also send a
        // load-time voice nudge-step sync (tuning-step.js), which is not ours.
        const w = (await page.writes()).filter(x => x.path.startsWith('/api/feature-setup/'));
        expect(w.length === 1 && w[0].path === '/api/feature-setup/dx-cluster'
            && JSON.stringify(w[0].body) === JSON.stringify({ enabled: true, host: 'dx.example.org', port: 7300, callsign: 'n0call' }),
            `wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
});

// Diagnostics "Start fresh test log": the mark goes to the server (stubbed
// here), the download links point at the log endpoint.
test('/Diagnostics: Start fresh test log marks the log, the download links fetch it', async () => {
    const page = await open('/Diagnostics', { settleMs: 1500 });
    try {
        const links = await page.eval(`[...document.querySelectorAll('a[href^="/api/diagnostics/log"]')].map(a => a.getAttribute('href'))`);
        expect(links.includes('/api/diagnostics/log') && links.includes('/api/diagnostics/log?full=true'), `links ${JSON.stringify(links)}`);
        await page.eval(`document.getElementById('diag-mark-log').click()`);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the mark' });
        const w = await page.writes();
        expect(w.length === 1 && w[0].method === 'POST' && w[0].path === '/api/diagnostics/log/mark', `wrote ${JSON.stringify(w)}`);
        await page.waitFor(`/Fresh capture started/.test(document.getElementById('diag-mark-result').textContent)`, { what: 'the confirmation' });
    } finally { await page.close(); }
});

const dialogPos = page => page.eval(`(() => { const r = document.getElementById('dxSpotsDialog').getBoundingClientRect(); return { x: r.left, y: r.top }; })()`);
const allBands = page => page.eval(`document.getElementById('dxSpotsAllBandsChk').checked`);

test('Index: All bands switch toggles from its input and its label', async () => {
    const page = await indexWithSpot();
    try {
        const was = await allBands(page);
        await page.click('#dxSpotsAllBandsChk');
        expect(await allBands(page) === !was, 'clicking the switch itself did not toggle it');
        await page.click('label[for="dxSpotsAllBandsChk"]');
        expect(await allBands(page) === was, 'clicking the label did not toggle it');
    } finally { await page.close(); }
});

test('Index: a click on the All bands switch padding toggles it and does not drag', async note => {
    const page = await indexWithSpot();
    try {
        // A point inside the switch's div that is the div itself, not the
        // input or the label - the gap the README "Fixed since" row is about.
        const pt = await page.eval(`(() => {
            const div = document.getElementById('dxSpotsAllBandsChk').closest('.form-switch');
            const r = div.getBoundingClientRect();
            for (let y = r.top + 1; y < r.bottom; y += 2)
                for (let x = r.left + 1; x < r.right; x += 2)
                    if (document.elementFromPoint(x, y) === div) return { x, y };
            return null;
        })()`);
        if (!pt) { note('the switch div has no bare padding to click - every point is the input or label'); return; }
        const pos0 = await dialogPos(page);
        const was = await allBands(page);
        await page.drag(pt, { x: pt.x + 3, y: pt.y + 2 }, 2);   // a click with the slight wobble a real hand has
        const pos1 = await dialogPos(page);
        expect(Math.abs(pos1.x - pos0.x) < 1 && Math.abs(pos1.y - pos0.y) < 1, `the dialog moved by ${pos1.x - pos0.x}, ${pos1.y - pos0.y}`);
        expect(await allBands(page) === !was, `a click at (${pt.x}, ${pt.y}) on the switch padding did not toggle it`);
    } finally { await page.close(); }
});

// Keyboard shortcuts. Keys go in as keydown events on the document, which is
// what the shortcut listener sees; every write is stubbed.
const press = (page, key, code, mods = {}) => page.eval(`document.dispatchEvent(new KeyboardEvent('keydown',
    ${JSON.stringify({ key, code, bubbles: true, cancelable: true, ...mods })}))`);
const shortcutsPage = async () => {
    const page = await open('/', { settleMs: 2500 });
    await page.waitFor(`getComputedStyle(document.getElementById('initOverlay')).display === 'none'`,
        { timeoutMs: 16000, what: 'start-up overlay gone' });
    await page.state('FrequencyA', 14_200_000);
    await page.state('ModeA', 'LSB');
    await page.eval(`document.activeElement?.blur?.()`);
    await page.clearWrites();
    return page;
};

test('Keys: u sets USB and i tunes up 100 Hz on VFO A', async () => {
    const page = await shortcutsPage();
    try {
        // From what the page holds, not 14.2 MHz: a live radio's own
        // FrequencyA broadcast can land after the test's.
        const was = await page.eval(`window.radioControl._state.lastBackendFreq.A`);
        await press(page, 'u', 'KeyU');
        await press(page, 'i', 'KeyI');
        await sleep(400);
        const ws = await page.writes();
        const m = modeWrites(ws), f = freqWrites(ws);
        expect(m.length === 1 && m[0].body.mode === '2', `expected one mode write of USB ("2"), got ${JSON.stringify(m)}`);
        expect(f.length === 1 && f[0].body.frequencyHz === was + 100, `expected one frequency write of ${was + 100}, got ${JSON.stringify(f)}`);
    } finally { await page.close(); }
});

test('Keys: n switches the active VFO to B', async () => {
    const page = await shortcutsPage();
    try {
        await press(page, 'n', 'KeyN');
        await sleep(400);
        const v = writesTo(await page.writes(), '/api/cat/active-vfo/');
        expect(v.length === 1 && v[0].path.toLowerCase().endsWith('/b'), `expected one write to active-vfo/b, got ${JSON.stringify(v)}`);
    } finally { await page.close(); }
});

test('Keys: ? opens the list, keys do nothing while it is open, Esc closes it', async () => {
    const page = await shortcutsPage();
    try {
        await press(page, '?', 'Slash', { shiftKey: true });
        await page.waitFor(`document.getElementById('keyboardShortcutsDialog')?.open`, { timeoutMs: 2000, what: 'shortcuts dialog open' });
        const rows = await page.eval(`document.querySelectorAll('#keyboardShortcutsDialog tbody tr').length`);
        expect(rows > 20, `only ${rows} rows in the shortcuts list`);
        await press(page, 'u', 'KeyU');
        await press(page, 'i', 'KeyI');
        await sleep(400);
        const ws = await page.writes();
        expect(modeWrites(ws).length === 0 && freqWrites(ws).length === 0, `a key reached the radio under the help list: ${JSON.stringify(ws)}`);
        await press(page, 'Escape', 'Escape');
        await page.waitFor(`!document.getElementById('keyboardShortcutsDialog').open`, { timeoutMs: 2000, what: 'shortcuts dialog closed' });
    } finally { await page.close(); }
});

test('Keys: nothing fires while typing in a text field', async () => {
    const page = await shortcutsPage();
    try {
        await page.eval(`(() => { document.getElementById('dxWatchDialog').show(); document.getElementById('dxWatchNewInput').focus(); })()`);
        await press(page, 'u', 'KeyU');
        await sleep(400);
        expect(modeWrites(await page.writes()).length === 0, 'u in the DX Watch box changed the mode');
    } finally { await page.close(); }
});

test('Keys: the configured TX key is never a shortcut as well', async () => {
    const page = await shortcutsPage();
    try {
        await page.eval(`window.iwcTxToggleKey = 'u'`);
        await press(page, 'u', 'KeyU');
        await sleep(400);
        expect(modeWrites(await page.writes()).length === 0, 'u changed the mode while it is the TX key');
    } finally { await page.close(); }
});

test('Index: dragging the DX Spots title still moves the dialog', async () => {
    const page = await indexWithSpot();
    try {
        const from = await page.pointOf('#dxSpotsTitle');
        const pos0 = await dialogPos(page);
        await page.drag(from, { x: from.x + 120, y: from.y + 60 });
        const pos1 = await dialogPos(page);
        expect(Math.abs(pos1.x - pos0.x - 120) < 4 && Math.abs(pos1.y - pos0.y - 60) < 4,
            `expected a move of 120, 60; got ${pos1.x - pos0.x}, ${pos1.y - pos0.y}`);
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- /radio
// The shared radio panel with IWC's adapter. The radio "is" whatever these
// tests emit; every write is stubbed and checked for its URL and body.

// A button inside `scope` whose text (or, failing that, title) starts with `label`.
const clickButton = async (page, scope, label) => {
    const p = await page.eval(`(() => {
        const bs = [...document.querySelectorAll(${JSON.stringify(scope + ' button')})];
        const b = bs.find(b => b.textContent.trim() === ${JSON.stringify(label)})
            ?? bs.find(b => b.textContent.trim().startsWith(${JSON.stringify(label)}));
        if (!b) return null;
        const r = b.getBoundingClientRect();
        return { x: r.left + r.width / 2, y: r.top + r.height / 2 };
    })()`);
    if (!p) throw new Error(`no button "${label}" in ${scope}`);
    await page.click(p);
};
// The radio's own channels, as GET /api/memory/radio-channels gives them, so no
// test reads the real radio's memories.
const RADIO_CHANNELS = [
    { channel: 1, isEmpty: false, frequencyHz: 7_074_000, mode: 'USB', name: 'FT8 40' },
    { channel: 2, isEmpty: true, frequencyHz: 0, mode: '', name: '' },
    { channel: 3, isEmpty: false, frequencyHz: 14_025_000, mode: 'CW-U', name: '' },
];
const radioPage = async (channels = RADIO_CHANNELS) => {
    const page = await open('/radio', {
        settleMs: 2000,
        before: `__rwcTest.getOverrides['/api/memory/radio-channels'] = ${JSON.stringify({ channels, misses: 0 })};`,
    });
    await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the adapter hub handler' });
    for (const [p, v] of [['IsConnected', true], ['ActiveVfo', 0], ['FrequencyA', 14_074_000], ['BandA', '20m'],
        ['ModeA', 'USB'], ['IfWidthA', '2400'], ['SelectedFilterA', '1'], ['AgcA', '2'], ['AttA', '00'], ['IpoA', '1']])
        await page.state(p, v);
    await page.clearWrites();
    return page;
};
const writesTo = (ws, prefix) => ws.filter(w => w.path.toLowerCase().startsWith(prefix));
// Anything that keys the radio, or switches it off.
const FORBIDDEN = /\/api\/cat\/(tx|vox|atu|cw\/send|radiopower)\b/i;

test('/radio: live state reaches the VFO box', async () => {
    const page = await radioPage();
    try {
        const freq = await text(page, '.rp-freq');
        expect(/14\.074/.test(freq), `frequency shows "${freq}"`);
        const chips = await page.eval(`[...document.querySelectorAll('.rp-chips')[0].children].map(c => c.textContent).join('|')`);
        expect(/USB/.test(chips) && /2\.4/.test(chips) && /MID/.test(chips), `VFO-A chips are "${chips}"`);
        const flags = chips;
        expect(/P\.AMP1/.test(flags), `preamp flag missing from "${flags}"`);
    } finally { await page.close(); }
});

test('/radio: FILTER key shows the slot and its width', async () => {
    const page = await radioPage();
    try {
        const sub = () => page.eval(`[...document.querySelectorAll('.rp-gkeys button')].find(b => /FILTER/.test(b.textContent))?.querySelector('.rp-sub')?.textContent ?? 'no key'`);
        let t = await sub();
        expect(/FIL1/.test(t) && /2\.4 kHz/.test(t), `FILTER key says "${t}"`);
        await page.state('SelectedFilterA', '2');
        await page.state('IfWidthA', '3600');
        t = await sub();
        expect(/FIL2/.test(t) && /3\.6 kHz/.test(t), `after FIL2 the key says "${t}"`);
    } finally { await page.close(); }
});

test('/radio: band, mode and receiver keys send IWC bodies', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-bands', '40');
        await clickButton(page, '.rp-modes', 'CW');
        await clickButton(page, '.rp-gkeys', 'ATT');
        await clickButton(page, '.rp-gkeys', 'P.AMP');
        await clickButton(page, '.rp-gkeys', 'AGC');
        await clickButton(page, '.rp-gkeys', 'FILTER');
        const ws = await page.writes();
        const one = (prefix, want) => {
            const w = writesTo(ws, prefix);
            expect(w.length === 1, `${prefix}: ${w.length} writes`);
            expect(JSON.stringify(w[0].body) === JSON.stringify(want), `${prefix} sent ${JSON.stringify(w[0].body)}, want ${JSON.stringify(want)}`);
        };
        one('/api/cat/band/a', { band: '40m' });
        one('/api/cat/mode/a', { mode: 'CW-U' });
        one('/api/cat/attenuator/a', { code: '20' });
        one('/api/cat/ipo/a', { code: '2' });
        one('/api/cat/agc/a', { code: '3' });
        one('/api/radio/filter/a', { fil: 2 });
    } finally { await page.close(); }
});

test('/radio: selecting VFO B moves the keys and settings to B', async () => {
    const page = await radioPage();
    try {
        await page.state('ActiveVfo', 1);
        const name = await text(page, '.rp-group-A .rp-group-name');
        expect(/VFO-B/.test(name), `receiver group says "${name}"`);
        await clickButton(page, '.rp-gkeys', 'ATT');
        const ws = await page.writes();
        expect(writesTo(ws, '/api/cat/attenuator/b').length === 1, `ATT wrote ${JSON.stringify(ws.map(w => w.path))}`);
        // A press on the VFO A/B key asks for A again.
        await page.clearWrites();
        await clickButton(page, '.rp-panel', 'VFO A / B');
        expect(writesTo(await page.writes(), '/api/cat/active-vfo/a').length === 1, 'VFO A / B did not select A');
    } finally { await page.close(); }
});

test('/radio: the width chip lists the IC-7300 widths and sets one', async () => {
    const page = await radioPage();
    try {
        await page.click('.rp-chips > span:nth-child(2)');
        const labels = await page.eval(`[...document.querySelectorAll('.rp-picker button')].map(b => b.textContent)`);
        expect(labels.length === 41, `${labels.length} SSB widths listed (want 41: 50 Hz - 3.6 kHz)`);
        expect(labels.includes('2.4 kHz') && labels.includes('50 Hz') && labels.includes('3.6 kHz'), `widths: ${labels.join(', ')}`);
        await clickButton(page, '.rp-picker', '1.8 kHz');
        const w = writesTo(await page.writes(), '/api/radio/ifwidth/a');
        expect(w.length === 1 && w[0].body.hz === 1800, `ifwidth wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
});

test('/radio: scope frames light the lamp and are drawn', async () => {
    const page = await radioPage();
    try {
        const bins = Array.from({ length: 475 }, (_, i) => (i === 237 ? -40 : -110));
        await page.state('SdrStatus', { sdrId: 'A', status: 'streaming' });
        for (let i = 0; i < 5; i++) await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_074_000, spanHz: 50_000, mode: 'Center' });
        const lamp = await page.eval(`[...document.querySelectorAll('.rp-lamp')].map(l => l.parentElement.textContent.trim() + ':' + l.className)`);
        expect(lamp.some(l => /SCOPE:.*\bon\b/.test(l)), `lamps: ${lamp.join(', ')}`);
    } finally { await page.close(); }
});

// The cross-band peek only runs while some page reports the watch pane on
// screen; the board must report its spectrum tab, not only Classic.
test('/radio: the spectrum tabs tell the hub which panes are on screen', async () => {
    const page = await radioPage();
    try {
        await page.eval(`(() => {
            const conn = __rwcTest.handlers.find(h => h.name === 'radiostateupdate').conn;
            const invoke = conn.invoke;
            window.__panes = [];
            conn.invoke = function (name, ...args) {
                if (name === 'SpectrumPanels') window.__panes.push(args.join(','));
                return invoke.call(this, name, ...args);
            };
        })()`);
        const last = () => page.eval(`window.__panes[window.__panes.length - 1] ?? ''`);
        for (const [tab, want] of [['VFO-B', 'false,true'], ['Both', 'true,true'], ['VFO-A', 'true,false']]) {
            await clickButton(page, '.rp-viewtabs', tab);
            await sleep(1500);
            const got = await last();
            expect(got === want, `${tab} tab reported SpectrumPanels(${got}), want (${want})`);
        }
    } finally { await page.close(); }
});

// X-WATCH / X-Watch: cross-band watch and its peek interval, which are no
// longer in Settings. Only there with pseudo-dual on in Settings.
const XW_ON = { enabled: true, intervalSeconds: 15 }, XW_OFF = { enabled: false, intervalSeconds: 15 };
test('/radio: X-WATCH lists Off and the peek intervals and sets them', async note => {
    const page = await radioPage();
    try {
        const has = await page.eval(`[...document.querySelectorAll('.rp-keys button')].some(b => b.textContent.startsWith('X-WATCH'))`);
        if (!has) { note('no X-WATCH key: pseudo-dual is off in Settings'); return; }
        await page.state('CrossBandWatch', XW_ON);
        const sub = await page.eval(`[...document.querySelectorAll('.rp-keys button')].find(b => b.textContent.startsWith('X-WATCH')).textContent`);
        expect(/15 s/.test(sub), `the key says "${sub}" with it on at 15 s`);
        for (const [item, want] of [['Off', { enabled: false }], ['30 s', { enabled: true, intervalSeconds: 30 }]]) {
            await page.clearWrites();
            await clickButton(page, '.rp-keys', 'X-WATCH');
            await clickButton(page, '.rp-keylist', item);
            const w = writesTo(await page.writes(), '/api/radio/crossband-watch');
            expect(w.length === 1 && JSON.stringify(w[0].body) === JSON.stringify(want), `${item} wrote ${JSON.stringify(w)}, want ${JSON.stringify(want)}`);
        }
    } finally { await page.close(); }
});

test('/radio: with X-WATCH off and the VFOs on different bands, only the heard VFO shows', async note => {
    const page = await radioPage();
    try {
        const has = await page.eval(`[...document.querySelectorAll('.rp-keys button')].some(b => b.textContent.startsWith('X-WATCH'))`);
        if (!has) { note('no X-WATCH key: pseudo-dual is off in Settings'); return; }
        const view = () => page.eval(`[...document.querySelectorAll('.rp-viewtab.on')].map(t => t.textContent).join()`);
        await page.state('CrossBandWatch', XW_ON);
        await page.state('BandB', '6m');
        await clickButton(page, '.rp-viewtabs', 'Both');
        await page.state('CrossBandWatch', XW_OFF);
        expect(/VFO-A/.test(await view()), `with it off, the spectrum tab is "${await view()}", want VFO-A`);
        await page.state('CrossBandWatch', XW_ON);
        expect(/Both|both/.test(await view()), `with it back on, the spectrum tab is "${await view()}", want Both`);
        // Same band: the watch costs nothing, so it stays.
        await page.state('CrossBandWatch', XW_OFF);
        await page.state('BandB', '20m');
        expect(/Both|both/.test(await view()), `on one band, the spectrum tab is "${await view()}", want Both`);
    } finally { await page.close(); }
});

// The watch VFO gets one frame per peek, on one band or two, so its waterfall
// would crawl: the trace is shown without it.
test('/radio: the watch VFO shows its trace without the waterfall', async note => {
    const page = await radioPage();
    try {
        const has = await page.eval(`[...document.querySelectorAll('.rp-keys button')].some(b => b.textContent.startsWith('X-WATCH'))`);
        if (!has) { note('no X-WATCH key: pseudo-dual is off in Settings'); return; }
        const bare = () => page.eval(`['A', 'B'].filter(v => document.querySelector('.rp-specpane-' + v)?.classList.contains('no-wf')).join()`);
        await page.state('ActiveVfo', 0);
        await page.state('BandA', '20m');
        await page.state('CrossBandWatch', XW_ON);
        await page.state('BandB', '6m');
        await clickButton(page, '.rp-viewtabs', 'Both');
        expect(await bare() === 'B', `X-WATCH on, two bands: no waterfall on "${await bare()}", want B`);
        await page.state('ActiveVfo', 1);
        expect(await bare() === 'A', `listening on B: no waterfall on "${await bare()}", want A`);
        // One band too: the peeks still refresh it, so it would still crawl.
        await page.state('BandB', '20m');
        expect(await bare() === 'A', `one band: no waterfall on "${await bare()}", want A`);
        await page.state('ActiveVfo', 0);
        await page.state('CrossBandWatch', XW_OFF);
        expect(await bare() === 'B', `X-WATCH off, one band: no waterfall on "${await bare()}", want B`);
        await page.state('BandB', '6m');
    } finally { await page.close(); }
});

test('/radio: out of range with X-WATCH on says Waiting for data', async note => {
    const page = await radioPage();
    try {
        const has = await page.eval(`[...document.querySelectorAll('.rp-keys button')].some(b => b.textContent.startsWith('X-WATCH'))`);
        if (!has) { note('no X-WATCH key: pseudo-dual is off in Settings'); return; }
        const msg = () => page.eval(`document.querySelector('.rp-specpane-B .rp-spec-empty')?.textContent ?? 'none'`);
        await page.state('ActiveVfo', 0);
        await page.state('BandA', '20m');
        await page.state('BandB', '20m');
        await page.state('SdrStatus', { sdrId: 'B', status: 'outofrange' });
        await page.state('CrossBandWatch', XW_ON);
        expect(await msg() === 'Waiting for data', `X-WATCH on after the status: B says "${await msg()}"`);
        await page.state('CrossBandWatch', XW_OFF);
        expect(/outside the scope span/.test(await msg()), `X-WATCH off: B says "${await msg()}"`);
        await page.state('CrossBandWatch', XW_ON);
    } finally { await page.close(); }
});

test('Index: X-Watch switches cross-band watch, lists intervals, sits beside Listening', async note => {
    const page = await open('/', { settleMs: 2000 });
    try {
        const shown = await page.eval(`(document.getElementById('crossBandWatchGroup')?.style.display ?? 'none') !== 'none'`);
        if (!shown) { note('no X-Watch key: pseudo-dual is off in Settings'); return; }
        const key = () => text(page, '#crossBandWatchBtn');
        await page.state('CrossBandWatch', XW_OFF);
        expect(await key() === 'X-Watch Off', `key says "${await key()}" with it off`);
        await page.clearWrites();
        await page.eval(`document.getElementById('crossBandWatchBtn').click()`);
        let w = writesTo(await page.writes(), '/api/radio/crossband-watch');
        expect(w.length === 1 && w[0].body.enabled === true, `X-Watch wrote ${JSON.stringify(w)}`);
        expect(await key() === 'X-Watch 15 s', `key says "${await key()}" after the click`);
        await page.clearWrites();
        await page.eval(`[...document.querySelectorAll('#crossBandWatchIntervals button')].find(b => b.dataset.sec === '45').click()`);
        w = writesTo(await page.writes(), '/api/radio/crossband-watch');
        expect(w.length === 1 && w[0].body.intervalSeconds === 45, `Peek every 45 s wrote ${JSON.stringify(w)}`);
        for (const vfo of [1, 0]) {
            await page.state('ActiveVfo', vfo);
            const where = await page.eval(`document.getElementById('crossBandWatchGroup').previousElementSibling?.dataset.vfo`);
            expect(where === (vfo ? 'B' : 'A'), `listening on ${vfo ? 'B' : 'A'}, X-Watch is beside panel ${where}`);
        }
        await page.state('BandA', '20m');
        await page.state('BandB', '6m');
        await page.state('CrossBandWatch', XW_OFF);
        expect(await page.eval(`window.__iwcWatchCollapse()`) === true, 'off, on two bands: the watch panel is not collapsed');
        await page.state('CrossBandWatch', XW_ON);
        expect(await page.eval(`window.__iwcWatchCollapse()`) === false, 'on: the watch panel is still collapsed');
        expect(await page.eval(`window.__iwcWatchPeeks()`) === true, 'on, on two bands: the watch is not peeked');
        await page.state('BandB', '20m');
        expect(await page.eval(`window.__iwcWatchPeeks()`) === true, 'on, one band: the watch panel has its waterfall back');
    } finally { await page.close(); }
});

test('/radio: the main dial says which VFO it tunes, in the colour of that VFO', async () => {
    const page = await radioPage();
    try {
        const tag = () => page.eval(`(() => { const t = document.querySelector('.rp-dial-vfo'); return t ? t.textContent + ' ' + getComputedStyle(t).color : 'none'; })()`);
        await page.state('ActiveVfo', 0);
        const a = await tag();
        expect(a.startsWith('A '), `listening on A, the dial says "${a}"`);
        await page.state('ActiveVfo', 1);
        const b = await tag();
        expect(b.startsWith('B '), `listening on B, the dial says "${b}"`);
        expect(a.slice(2) !== b.slice(2), `A and B are the same colour (${a.slice(2)})`);
        await page.state('ActiveVfo', 0);
    } finally { await page.close(); }
});

test('/radio: the corner grip scales the board and keeps the size', async () => {
    const page = await radioPage();
    try {
        const got = await page.eval(`(() => {
            const g = document.querySelector('.rp-board-grip'), sizer = document.querySelector('.rp-sizer');
            if (!g) return 'no grip';
            const sel = [...document.querySelectorAll('select.rp-select')].find(s => s.title === 'Size');
            const before = sizer.getBoundingClientRect();
            const ev = (type, x, y) => g.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerId: 1, clientX: x, clientY: y }));
            ev('pointerdown', before.right - 5, before.bottom - 5);
            ev('pointermove', before.right - 5, before.top + before.height * 0.6);
            ev('pointerup', 0, 0);
            const after = sizer.getBoundingClientRect();
            const size = sel.value, opt = sel.selectedOptions[0]?.textContent;
            return JSON.stringify({ shrank: after.height < before.height * 0.8, ratio: Math.abs(after.width / after.height - before.width / before.height) < 0.02,
                size, opt, saved: localStorage.getItem('iwc.radioPanel.size') });
        })()`);
        const r = JSON.parse(got.startsWith('{') ? got : '{}');
        expect(got.startsWith('{'), got);
        expect(r.shrank && r.ratio, `the board did not shrink in shape: ${got}`);
        expect(/^\d+$/.test(r.size) && r.opt === r.size + '%' && r.saved === r.size, `the size picker and store say ${got}`);
        await page.eval(`localStorage.setItem('iwc.radioPanel.size', 'fit')`);
    } finally { await page.close(); }
});

test('/radio: M.STORE saves the selected VFO to IWC memories', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', 'M.STORE');
        const w = writesTo(await page.writes(), '/api/memory/save-vfo/a');
        expect(w.length === 1, `M.STORE wrote ${w.length}`);
        await page.waitFor(`[...document.querySelectorAll('.rp-mem-keys .rp-sub')].some(s => s.textContent === 'SAVED')`, { what: 'SAVED on M.STORE' });
    } finally { await page.close(); }
});

// The radio-memory keys' labels (the arrow is U+25B8).
const M_TO_VFO = 'M\u25B8VFO', VFO_TO_M = 'VFO\u25B8M';
const keySub = (page, label) => page.eval(`(() => {
    const b = [...document.querySelectorAll('.rp-mem-keys button')].find(b => b.textContent.trim().startsWith(${JSON.stringify(label)}));
    return b?.querySelector('.rp-sub')?.textContent ?? null;
})()`);
const LIST_ITEMS = `document.querySelectorAll('.rp-keylist:not([hidden]) .rp-menu-item')`;
const keyListItems = page => page.eval(`[...${LIST_ITEMS}].map(b => b.textContent)`);
const waitForList = page => page.waitFor(`${LIST_ITEMS}.length > 0`, { what: 'the channel list' });

test('/radio: M>VFO lists the programmed radio channels and tunes to one', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', M_TO_VFO);
        await waitForList(page);
        const items = await keyListItems(page);
        expect(items.length === 2 && /^M01 .*7\.074 USB .*FT8 40/.test(items[0]) && /^M03 .*14\.025 CW-U/.test(items[1]),
            `listed ${JSON.stringify(items)} (the empty M02 should not be)`);
        await clickButton(page, '.rp-keylist', 'M03');
        const w = (await page.writes()).filter(x => /radio-channel/.test(x.path));
        expect(w.length === 1 && w[0].path === '/api/memory/radio-channel/3/to-vfo/a', `wrote ${JSON.stringify(w)}`);
        await page.waitFor(`[...document.querySelectorAll('.rp-mem-keys .rp-sub')].some(s => s.textContent === 'M03')`, { what: 'M03 on the key' });
    } finally { await page.close(); }
});

test('/radio: M>VFO pressed again closes its list', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', M_TO_VFO);
        await waitForList(page);
        await clickButton(page, '.rp-mem-keys', M_TO_VFO);
        expect(await page.eval(`document.querySelector('.rp-keylist').hidden`), 'the list is still open');
        expect((await page.writes()).length === 0, 'something was written');
    } finally { await page.close(); }
});

test('/radio: VFO>M writes only on a second press, naming what it saw', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', VFO_TO_M);
        await waitForList(page);
        const items = await keyListItems(page);
        expect(items.length === 3 && /^M02 .*\(empty\)/.test(items[1]), `listed ${JSON.stringify(items)}`);
        await clickButton(page, '.rp-keylist', 'M01');
        expect((await page.writes()).length === 0, 'picking a channel wrote at once');
        expect(await keySub(page, VFO_TO_M) === 'M01?', `key says "${await keySub(page, VFO_TO_M)}"`);
        await clickButton(page, '.rp-mem-keys', VFO_TO_M);
        await page.waitFor(`__rwcTest.writes.length > 0`, { what: 'the write' });
        const w = await page.writes();
        expect(w.length === 1 && w[0].path === '/api/memory/radio-channel/1/from-vfo/a', `wrote ${JSON.stringify(w)}`);
        expect(JSON.stringify(w[0].body) === JSON.stringify({ isEmpty: false, frequencyHz: 7_074_000, mode: 'USB' }), `sent ${JSON.stringify(w[0].body)}`);
    } finally { await page.close(); }
});

test('/radio: a VFO>M pick left for 4 s is dropped, not written', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', VFO_TO_M);
        await waitForList(page);
        await clickButton(page, '.rp-keylist', 'M02');
        await new Promise(r => setTimeout(r, 4300));
        expect(await keySub(page, VFO_TO_M) === '', `key still says "${await keySub(page, VFO_TO_M)}"`);
        await clickButton(page, '.rp-mem-keys', VFO_TO_M);
        await waitForList(page);
        expect((await page.writes()).length === 0, 'it wrote after the pick lapsed');
    } finally { await page.close(); }
});

// What counts is whether M99 can be seen: the panel's sizer clips at the
// panel's own bottom edge, which is above the window's when the panel is
// shorter than the window (Colin's bench, 2026-10-17: only M1-M12 reachable).
for (const size of ['fit', '50'])
    test(`/radio: all 99 radio channels can be scrolled to and seen (size ${size})`, async () => {
        const all = Array.from({ length: 99 }, (_, i) => ({ channel: i + 1, isEmpty: true, frequencyHz: 0, mode: '', name: '' }));
        const page = await radioPage(all);
        try {
            await page.eval(`(() => {
                const sel = [...document.querySelectorAll('select')].find(x => x.title === 'Size');
                sel.value = ${JSON.stringify(size)};
                sel.dispatchEvent(new Event('change'));
            })()`);
            await clickButton(page, '.rp-mem-keys', VFO_TO_M);
            await waitForList(page);
            const seen = await page.eval(`(() => {
                const kl = document.querySelector('.rp-keylist');
                kl.scrollTop = kl.scrollHeight;
                const items = [...kl.querySelectorAll('.rp-menu-item')];
                const at = el => { const r = el.getBoundingClientRect(); return document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); };
                return { last: at(items.at(-1)) === items.at(-1), text: items.at(-1).textContent };
            })()`);
            expect(seen.last && /^M99/.test(seen.text), `"${seen.text}" is hidden after scrolling the list to the end`);
            // and the top of the list, scrolled back
            const top = await page.eval(`(() => {
                const kl = document.querySelector('.rp-keylist');
                kl.scrollTop = 0;
                const first = kl.querySelector('.rp-menu-item'), r = first.getBoundingClientRect();
                return document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2) === first;
            })()`);
            expect(top, 'M01 is hidden with the list scrolled to the top');
        } finally { await page.close(); }
    });

test('/radio: has no key that transmits, and nothing clicked here wrote one', async () => {
    const page = await radioPage();
    try {
        const labels = await page.eval(`[...document.querySelectorAll('button')].map(b => b.textContent.trim())`);
        for (const bad of ['TUNE', 'VOX', 'MOX', 'PTT', 'TX'])
            expect(!labels.includes(bad), `a "${bad}" key is on the page`);
        // Every key, not the menus or view links (those navigate away).
        const keys = '.rp-gkeys button, .rp-top button.rp-key, .rp-mem-keys button, button.rp-key';
        const n = await page.eval(`document.querySelectorAll(${JSON.stringify(keys)}).length`);
        expect(n > 15, `only ${n} keys found`);
        for (let i = 0; i < n; i++)
            await page.eval(`document.querySelectorAll(${JSON.stringify(keys)})[${i}].click()`);
        const bad = (await page.writes()).filter(w => FORBIDDEN.test(w.path));
        expect(bad.length === 0, `transmit writes: ${JSON.stringify(bad)}`);
    } finally { await page.close(); }
});

test('/radio: VOICE key listens while held and shows what the recogniser is doing', async note => {
    const page = await radioPage();
    try {
        const sel = `[...document.querySelectorAll('button.rp-key')].find(b => b.textContent.includes('VOICE'))`;
        if (!await page.eval(`!!${sel}`)) { note('voice control is off in this install, so there is no VOICE key'); return; }
        const press = type => page.eval(`${sel}.dispatchEvent(new PointerEvent('${type}', { bubbles: true, button: 0, pointerId: 1 }))`);
        const look = () => page.eval(`(() => { const b = ${sel}; return b.querySelector('.rp-led').className + '|' + b.querySelector('.rp-sub').textContent; })()`);
        const voice = state => page.emit('VoiceStatusUpdate', { state, lastHeard: null, lastIntent: null, errorMessage: null, vfo: 'A' });
        await press('pointerdown');
        expect(writesTo(await page.writes(), '/api/voice/').map(w => w.path).join() === '/api/voice/start?vfo=A',
            `press sent ${JSON.stringify(await page.writes())}`);
        await voice('Listening');
        expect(await look() === 'rp-led on|LISTENING', `while listening the key shows "${await look()}"`);
        await press('pointerup');
        const paths = writesTo(await page.writes(), '/api/voice/').map(w => w.path);
        expect(paths.join() === '/api/voice/start?vfo=A,/api/voice/stop', `press and release sent ${paths.join(', ')}`);
        await voice('Idle');
        expect(await look() === 'rp-led|', `back at idle the key shows "${await look()}"`);
    } finally { await page.close(); }
});

test('/radio: band keys and mode keys each sit on one line, labels not clipped', async () => {
    const page = await radioPage();
    try {
        for (const row of ['.rp-bands', '.rp-modes']) {
            const r = await page.eval(`(() => {
                const bs = [...document.querySelectorAll('${row} button')];
                return { n: bs.length, tops: new Set(bs.map(b => Math.round(b.getBoundingClientRect().top))).size,
                    clipped: bs.filter(b => b.scrollWidth > b.clientWidth + 1).map(b => b.textContent) };
            })()`);
            expect(r.n > 0 && r.tops === 1, `${row}: ${r.n} keys on ${r.tops} lines`);
            expect(r.clipped.length === 0, `${row}: clipped labels ${r.clipped.join(', ')}`);
        }
    } finally { await page.close(); }
});

// The window keys (CW, RTTY, MEMORIES, DX, METERS, RX SETUP) and their windows.
const winShown = (page, id) => page.eval(`(() => {
    const f = document.querySelector('.rp-win[data-win="${id}"]');
    return !!f && !f.hidden && getComputedStyle(f).display !== 'none';
})()`);

test('/radio: every window key opens its window and its x shuts it', async () => {
    const page = await radioPage();
    try {
        const ids = await page.eval(`[...document.querySelectorAll('.rp-skey[data-group]')].map(b => b.dataset.group)`);
        expect(['cw', 'rtty', 'mem', 'dx', 'meters', 'rx'].every(id => ids.includes(id)), `window keys: ${ids.join(', ')}`);
        for (const id of ids) {
            await page.eval(`document.querySelector('.rp-skey[data-group="${id}"]').click()`);
            expect(await winShown(page, id), `${id} window did not open`);
            await page.eval(`document.querySelector('.rp-win[data-win="${id}"] .rp-win-x').click()`);
            expect(!(await winShown(page, id)), `${id} window did not shut`);
        }
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/radio: opening the CW window switches to CW, RTTY to RTTY-L', async () => {
    const page = await radioPage();
    try {
        await page.eval(`document.querySelector('.rp-skey[data-group="cw"]').click()`);
        let w = writesTo(await page.writes(), '/api/cat/mode/a');
        expect(w.length === 1 && w[0].body.mode === 'CW-U', `CW window wrote ${JSON.stringify(w)}`);
        await page.clearWrites();
        await page.eval(`document.querySelector('.rp-skey[data-group="rtty"]').click()`);
        w = writesTo(await page.writes(), '/api/cat/mode/a');
        expect(w.length === 1 && w[0].body.mode === 'RTTY-L', `RTTY window wrote ${JSON.stringify(w)}`);
    } finally { await page.close(); }
});

test('/radio: the CW keyer sets speed and pitch, never sends', async () => {
    const page = await radioPage();
    try {
        await page.state('CwSpeed', 20);
        await page.state('CwPitch', 600);
        await page.eval(`document.querySelector('.rp-skey[data-group="cw"]').click()`);
        await page.clearWrites();
        // The keyer's two "+" keys: speed first, then pitch.
        await page.eval(`[...document.querySelectorAll('[data-panel="keyer"] button')].filter(b => b.textContent.trim() === '+').forEach(b => b.click())`);
        const ws = await page.writes();
        const sp = writesTo(ws, '/api/cat/cw/speed'), pi = writesTo(ws, '/api/cat/cwpitch');
        expect(sp.length === 1 && sp[0].body.speed === 21, `speed wrote ${JSON.stringify(sp)}`);
        expect(pi.length === 1 && pi[0].body.hz === 610, `pitch wrote ${JSON.stringify(pi)}`);
        expect(!ws.some(w => FORBIDDEN.test(w.path)), `transmit writes: ${JSON.stringify(ws)}`);
    } finally { await page.close(); }
});

test('/radio: the mouse wheel steps the CW keyer speed and pitch', async () => {
    const page = await radioPage();
    try {
        await page.state('CwSpeed', 20);
        await page.state('CwPitch', 600);
        await page.eval(`document.querySelector('.rp-skey[data-group="cw"]').click()`);
        await page.clearWrites();
        // Wheel up over SPEED, down over PITCH (rows 1 and 2 of the keyer).
        await page.eval(`(() => {
            const rows = [...document.querySelectorAll('[data-panel="keyer"] .rp-wrow')];
            const wheel = (el, dy) => el.dispatchEvent(new WheelEvent('wheel', { deltaY: dy, bubbles: true, cancelable: true }));
            wheel(rows[0], -100);
            wheel(rows[1], 100);
        })()`);
        const ws = await page.writes();
        const sp = writesTo(ws, '/api/cat/cw/speed'), pi = writesTo(ws, '/api/cat/cwpitch');
        expect(sp.length === 1 && sp[0].body.speed === 21, `speed wrote ${JSON.stringify(sp)}`);
        expect(pi.length === 1 && pi[0].body.hz === 590, `pitch wrote ${JSON.stringify(pi)}`);
        expect(!ws.some(w => FORBIDDEN.test(w.path)), `transmit writes: ${JSON.stringify(ws)}`);
    } finally { await page.close(); }
});

test('/radio: the meters window shows the S-meter', async () => {
    const page = await radioPage();
    try {
        await page.eval(`document.querySelector('.rp-skey[data-group="meters"]').click()`);
        await page.state('SMeterA', 120);
        const txt = await page.eval(`document.querySelector('.rp-win[data-win="meters"] .rp-wbar .txt')?.textContent ?? ''`);
        expect(txt && txt !== '--', `S bar reads "${txt}"`);
    } finally { await page.close(); }
});

test('/RadioWindow?g=meters: draws its panels and follows live state', async () => {
    const page = await open('/RadioWindow?g=meters', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the window hub handler' });
        const titles = await page.eval(`[...document.querySelectorAll('.rp-wpop .rp-wpanel-title')].map(t => t.textContent)`);
        expect(titles.join('|') === 'ALL METERS|S-METER HISTORY', `panels: ${titles.join('|')}`);
        expect(await page.eval('__rwcTest.started'), 'the hub was never started, so nothing live reaches the window');
        await page.state('ActiveVfo', 0);
        await page.state('SMeterA', 120);
        const txt = await page.eval(`document.querySelector('.rp-wbar .txt')?.textContent ?? ''`);
        expect(txt && txt !== '--', `S bar reads "${txt}"`);
    } finally { await page.close(); }
});

test('/RadioWindow: an unknown group says so', async () => {
    const page = await open('/RadioWindow?g=nonsense', { settleMs: 800 });
    try {
        expect(/No such window/.test(await text(page, '#radioWindow')), 'no "No such window" text');
    } finally { await page.close(); }
});

test('/RadioSpectrum: span picker sets the radio scope span (half-width)', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        expect(await page.eval('__rwcTest.started'), 'the hub was never started');
        await page.state('FrequencyA', 14_074_000);
        const bins = Array.from({ length: 475 }, (_, i) => (i === 237 ? -40 : -110));
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_074_000, spanHz: 50_000, mode: 'Center' });
        await page.eval(`(() => { const s = document.querySelector('[data-pane="A"] select.rp-spop-span') ?? document.querySelector('select.rp-spop-span');
            s.value = '100000'; s.dispatchEvent(new Event('change')); })()`);
        const w = writesTo(await page.writes(), '/api/cat/scopespan/a');
        expect(w.length === 1 && w[0].body === 50000, `scopespan wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: a click tunes there, and the readout names it first', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.state('FrequencyA', 14_074_000);
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 14_074_000, spanHz: 10_000, mode: 'Center' });
        // Three quarters of the way across a 10 kHz span on 14.074 is 14.0765.
        const read = await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width * 0.75, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            const t = el.querySelector('.rp-xread').textContent;
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, clientX: x, clientY: y }));
            return t;
        })()`);
        expect(read === '14.076.500', `the readout said "${read}"`);
        await sleep(300);
        const w = writesTo(await page.writes(), '/api/cat/frequency/a');
        expect(w.length === 1 && w[0].body?.frequencyHz === 14_076_500, `frequency wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: the mouse wheel steps the dial by the board STEP', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.state('FrequencyA', 12_000_550);
        const bins = Array.from({ length: 475 }, () => -110);
        await page.state('SpectrumUpdate', { sdrId: 'A', bins, centreHz: 12_000_550, spanHz: 10_000, mode: 'Center' });
        await page.eval(`localStorage.setItem('iwc.radioPanel.stepHz', '1000')`);
        // Off the bands, so no DX spot is near to snap to. The pointer stays
        // mid-pane: the readout follows the trace as it slides.
        const reads = await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-spectrum');
            const rc = el.getBoundingClientRect(), x = rc.left + rc.width / 2, y = rc.top + rc.height / 2;
            el.dispatchEvent(new PointerEvent('pointermove', { bubbles: true, clientX: x, clientY: y }));
            const before = el.querySelector('.rp-xread').textContent;
            el.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, clientX: x, clientY: y, deltaY: -100 }));
            return [before, el.querySelector('.rp-xread').textContent];
        })()`);
        expect(reads[0] === '12.000.550' && reads[1] === '12.001.000', `the readout said ${JSON.stringify(reads)}`);
        await sleep(300);
        let w = writesTo(await page.writes(), '/api/cat/frequency/a');
        expect(w.length === 1 && w[0].body?.frequencyHz === 12_001_000, `a notch up wrote ${JSON.stringify(w)}`);
        await page.eval(`(() => {
            const el = document.querySelector('[data-pane="A"] .rp-waterfall');
            el.dispatchEvent(new WheelEvent('wheel', { bubbles: true, cancelable: true, deltaY: 100 }));
        })()`);
        await sleep(300);
        w = writesTo(await page.writes(), '/api/cat/frequency/a');
        expect(w.length === 2 && w[1].body?.frequencyHz === 12_000_000, `a notch down wrote ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.eval(`localStorage.removeItem('iwc.radioPanel.stepHz')`).catch(() => {}); await page.close(); }
});

test('/RadioSpectrum: with X-WATCH off and the VFOs on different bands, only the heard VFO shows', async note => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        const shown = () => page.eval(`[...document.querySelectorAll('.rp-spop-body [data-pane]')].filter(h => h.style.display !== 'none').map(h => h.dataset.pane).join()`);
        await page.eval(`[...document.querySelectorAll('.rp-spop-tab')].find(t => /both/i.test(t.textContent))?.click()`);
        if (await shown() !== 'A,B') { note(`no Both view (${await shown()}): pseudo-dual is off in Settings`); return; }
        await page.state('ActiveVfo', 0);
        await page.state('CrossBandWatch', { enabled: true, intervalSeconds: 15 });
        await page.state('BandA', '20m');
        await page.state('BandB', '6m');
        expect(await shown() === 'A,B', `X-WATCH on: panes ${await shown()}, want A,B`);
        await page.state('CrossBandWatch', { enabled: false, intervalSeconds: 15 });
        expect(await shown() === 'A', `X-WATCH off, two bands: panes ${await shown()}, want A`);
        await page.state('ActiveVfo', 1);
        expect(await shown() === 'B', `listening on B: panes ${await shown()}, want B`);
        await page.state('CrossBandWatch', { enabled: true, intervalSeconds: 15 });
        expect(await shown() === 'A,B', `X-WATCH back on: panes ${await shown()}, want A,B`);
        await page.state('CrossBandWatch', { enabled: false, intervalSeconds: 15 });
        await page.state('BandB', '20m');
        expect(await shown() === 'A,B', `one band: panes ${await shown()}, want A,B`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: the watch pane shows its trace without the waterfall', async note => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.eval(`[...document.querySelectorAll('.rp-spop-tab')].find(t => /both/i.test(t.textContent))?.click()`);
        const shown = await page.eval(`[...document.querySelectorAll('.rp-spop-body [data-pane]')].filter(h => h.style.display !== 'none').map(h => h.dataset.pane).join()`);
        if (shown !== 'A,B') { note(`no Both view (${shown}): pseudo-dual is off in Settings`); return; }
        const bare = () => page.eval(`['A', 'B'].filter(v => document.querySelector('.rp-spop-body [data-pane="' + v + '"] .rp-pane')?.classList.contains('no-wf')).join()`);
        await page.state('ActiveVfo', 0);
        await page.state('BandA', '20m');
        await page.state('CrossBandWatch', XW_ON);
        await page.state('BandB', '6m');
        expect(await bare() === 'B', `X-WATCH on, two bands: no waterfall on "${await bare()}", want B`);
        // It gives up the room its waterfall had, rather than a tall empty trace.
        const tall = v => page.eval(`document.querySelector('.rp-spop-body [data-pane="${v}"]').getBoundingClientRect().height`);
        expect(await tall('B') < (await tall('A')) * 0.6, `the trace-only pane is ${await tall('B')}px high beside ${await tall('A')}px`);
        await page.state('ActiveVfo', 1);
        expect(await bare() === 'A', `listening on B: no waterfall on "${await bare()}", want A`);
        await page.state('BandB', '20m');
        expect(await bare() === 'A', `one band: no waterfall on "${await bare()}", want A`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: out of range with X-WATCH on says Waiting for data', async note => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        await page.waitFor('__rwcTest.handlers.some(h => h.name === "radiostateupdate")', { what: 'the spectrum hub handler' });
        await page.eval(`[...document.querySelectorAll('.rp-spop-tab')].find(t => /both/i.test(t.textContent))?.click()`);
        const shown = await page.eval(`[...document.querySelectorAll('.rp-spop-body [data-pane]')].filter(h => h.style.display !== 'none').map(h => h.dataset.pane).join()`);
        if (shown !== 'A,B') { note(`no Both view (${shown}): pseudo-dual is off in Settings`); return; }
        const msg = () => page.eval(`document.querySelector('.rp-spop-body [data-pane="B"] .rp-spec-empty')?.textContent ?? 'none'`);
        await page.state('ActiveVfo', 0);
        await page.state('BandA', '20m');
        await page.state('BandB', '20m');
        await page.state('CrossBandWatch', XW_OFF);
        await page.state('SdrStatus', { sdrId: 'B', status: 'outofrange' });
        expect(/outside the scope span/.test(await msg()), `X-WATCH off: B says "${await msg()}"`);
        await page.state('CrossBandWatch', XW_ON);
        expect(await msg() === 'Waiting for data', `X-WATCH on: B says "${await msg()}"`);
    } finally { await page.close(); }
});

test('/RadioSpectrum: the corner grip resizes the window', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        const got = await page.eval(`(() => {
            const g = document.querySelector('.rp-spop-grip');
            if (!g) return 'no grip';
            const r = g.getBoundingClientRect();
            if (Math.abs(r.right - innerWidth) > 1 || Math.abs(r.bottom - innerHeight) > 1) return 'grip at ' + r.right + ',' + r.bottom;
            let to = null;
            window.resizeTo = (w, h) => { to = [w, h]; };
            g.setPointerCapture = () => {};
            const ev = (type, x, y) => g.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerId: 1, screenX: x, screenY: y }));
            ev('pointerdown', 500, 500); ev('pointermove', 560, 470); ev('pointerup', 560, 470);
            const want = [outerWidth + 60, Math.max(300, outerHeight - 30)];
            ev('pointermove', 900, 900);
            return to && to[0] === want[0] && to[1] === want[1] ? 'ok' : 'resizeTo ' + JSON.stringify(to) + ', want ' + JSON.stringify(want);
        })()`);
        expect(got === 'ok', got);
    } finally { await page.close(); }
});

test('/RadioSpectrum: SCOPE opens Range and the rest, saved for the board', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        const got = await page.eval(`(() => {
            const pane = document.querySelector('.rp-spop-body [data-pane="A"] .rp-pane');
            const key = pane && [...pane.querySelectorAll('.rp-spop-key')].find(b => b.textContent === 'SCOPE');
            if (!key) return 'no SCOPE key';
            const sets = pane.querySelector('.rp-spec-settings');
            if (getComputedStyle(sets).display !== 'none') return 'panel open before the click';
            key.click();
            if (getComputedStyle(sets).display === 'none' || !key.classList.contains('on')) return 'panel did not open';
            const r = sets.querySelector('input[data-set=range]');
            r.value = '42'; r.dispatchEvent(new Event('input'));
            const saved = JSON.parse(localStorage.getItem('iwc.radioPanel.spectrum.A') ?? '{}');
            if (saved.range !== 42) return 'saved ' + JSON.stringify(saved);
            if (r.closest('.rp-spec-row').querySelector('.rp-spec-val').textContent !== '42 dB') return 'readout not 42 dB';
            const untitled = [...sets.querySelectorAll('.rp-spec-row')].filter(x => !x.title).map(x => x.textContent);
            if (untitled.length) return 'no tooltip: ' + untitled.join();
            key.click();
            return getComputedStyle(sets).display === 'none' ? 'ok' : 'panel did not close';
        })()`);
        expect(got === 'ok', got);
    } finally { await page.close(); }
});

test('/RadioSpectrum: pane text is screen colour on the screen, in every theme', async () => {
    const page = await open('/RadioSpectrum', { settleMs: 1500 });
    try {
        const bad = await page.eval(`(async () => {
            const { THEMES, applyTheme } = await import('/js/radio-panel/radio-panel-themes.js');
            const rgb = c => (c.match(/[\d.]+/g) ?? []).slice(0, 3).map(Number);
            const lum = c => { const [r, g, b] = rgb(c).map(x => x / 255).map(x => x <= .03928 ? x / 12.92 : ((x + .055) / 1.055) ** 2.4); return .2126 * r + .7152 * g + .0722 * b; };
            const ratio = (a, b) => { const x = lum(a), y = lum(b); return (Math.max(x, y) + .05) / (Math.min(x, y) + .05); };
            const root = document.querySelector('.rp-spop');
            const pane = root.querySelector('.rp-pane');
            const out = [];
            for (const id of Object.keys(THEMES)) {
                if (id === 'night') continue; // dim on purpose
                applyTheme(root, id);
                const bg = getComputedStyle(pane).backgroundColor;
                for (const sel of ['.rp-pane-label', '.rp-axis']) {
                    const k = ratio(getComputedStyle(pane.querySelector(sel)).color, bg);
                    if (k < 4.5) out.push(id + ' ' + sel + ' ' + k.toFixed(1));
                }
                const sets = pane.querySelector('.rp-spec-settings');
                const k = ratio(getComputedStyle(sets).color, getComputedStyle(sets).backgroundColor);
                if (k < 4.5) out.push(id + ' SCOPE panel ' + k.toFixed(1));
            }
            return out.join('; ');
        })()`);
        expect(!bad, bad);
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- Experimental UI (/flexui)

// /flexui is Index moved into the shared dock (core/js/flex). These check the
// move, not the radio: the hub is stubbed as everywhere else in this file.
const flexWhere = (page, c) => page.eval(`(() => { const s = document.querySelector('[data-ywc-component="${c}"]'); if (!s) return 'gone'; return s.closest('#ywcFlexParking') ? 'parked' : s.closest('#ywcFlexHost') ? 'docked' : 'elsewhere'; })()`);
const openFlex = async () => {
    const page = await open('/flexui', { settleMs: 3500 });
    await page.eval(`document.querySelector('#initOverlay button')?.click()`);
    await sleep(500);
    return page;
};

test('flexui: every section is docked, with gauges sized', async () => {
    const page = await openFlex();
    try {
        for (const c of ['vfoActions', 'meters', 'spectrum', 'vfoA', 'vfoB']) {
            expect(await flexWhere(page, c) === 'docked', `${c} is ${await flexWhere(page, c)}, not docked`);
        }
        expect(await flexWhere(page, 'clarifier') === 'docked', 'clarifier is not in the dock');
        expect(!await page.eval(`document.body.classList.contains('iwc-flex-pending')`), 'the page is still marked pending');
        const unsized = await page.eval(`[...document.querySelectorAll('#ywcFlexHost [data-ywc-component="meters"] canvas')].filter(c => !c.width || !c.height).map(c => c.id)`);
        expect(unsized.length === 0, `meter canvases with no size: ${unsized.join(', ')}`);
        expect(await page.eval(`!!document.getElementById('vfoRow')?.dataset`), '#vfoRow (site.js reads the radio model from it) has gone');
    } finally { await page.close(); }
});

test('flexui: a hidden panel is parked and still gets live updates', async () => {
    const page = await openFlex();
    try {
        const toggle = () => page.eval(`[...document.querySelectorAll('#ywcFlexPanelsMenu a, #ywcFlexPanelsMenu button')].find(e => e.textContent.trim() === 'VFO B')?.click()`);
        await toggle(); await sleep(500);
        expect(await flexWhere(page, 'vfoB') === 'parked', 'VFO B was not parked when hidden');
        await page.state('FrequencyB', 7012345); await sleep(300);
        expect(await page.eval(`document.getElementById('vfoBCol').textContent.includes('7.012')`), 'parked VFO B did not take a frequency update');
        await toggle(); await sleep(600);
        expect(await flexWhere(page, 'vfoB') === 'docked', 'VFO B did not come back when shown');
    } finally { await page.close(); }
});

test('flexui: pop-out takes live updates and comes home with its canvases', async () => {
    const page = await openFlex();
    try {
        for (const [tab, el] of [['vfoA', 'vfoACol'], ['meters', 'meterCard']]) {
            await page.eval(`window.ywcFlex.model.doAction(window.FlexLayout.Actions.popoutTab('${tab}'))`);
            await page.waitFor(`document.getElementById('${el}')?.ownerDocument !== document`, { what: `${tab} to pop out` });
            await sleep(1500);   // the pop-out's listeners are attached once its window has loaded
            if (tab === 'vfoA') {
                await page.state('FrequencyA', 14074000); await sleep(300);
                expect(await page.eval(`document.getElementById('vfoACol').textContent.includes('14.074')`), 'popped-out VFO A did not take a frequency update');
            }
            await page.eval(`document.getElementById('${el}').ownerDocument.defaultView.close()`);
            await page.waitFor(`document.getElementById('${el}')?.ownerDocument === document && !!document.getElementById('${el}').closest('#ywcFlexHost')`, { what: `${tab} to dock again` });
        }
        const lost = await page.eval(`[...document.querySelectorAll('#ywcFlexHost canvas')].filter(c => c.getContext('2d')?.isContextLost?.()).map(c => c.id)`);
        expect(lost.length === 0, `canvases lost their context: ${lost.join(', ')}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('flexui: Save current as... writes the layout to /api/flexlayouts', async () => {
    const page = await openFlex();
    try {
        await page.eval(`window.prompt = () => 'ui test layout'`);
        await page.clearWrites();
        await page.eval(`[...document.querySelectorAll('#ywcFlexLayoutMenu a, #ywcFlexLayoutMenu button')].find(e => e.textContent.includes('Save current'))?.click()`);
        await sleep(600);
        const w = (await page.writes()).find(x => /^\/api\/flexlayouts/.test(x.path));
        expect(!!w, 'no write to /api/flexlayouts');
        expect(w.body?.name === 'ui test layout' && !!w.body?.layout?.layout, `unexpected body: ${JSON.stringify(w.body).slice(0, 200)}`);
    } finally { await page.close(); }
});

// ---------------------------------------------------------------- MIC gain

test('Index: MIC gain slider follows a MicGain update', async () => {
    const page = await open('/', { settleMs: 1500 });
    try {
        await page.state('MicGain', 37);
        await page.waitFor(`document.getElementById('micGainSlider').value === '37' && document.getElementById('micGainValue').textContent === '37'`,
            { timeoutMs: 2000, what: 'slider and label at 37' });
        expect(writesTo(await page.writes(), '/api/cat/micgain').length === 0, 'the update was written back to the radio');
    } finally { await page.close(); }
});

// Fabio's dark theme. A Classic rule loaded after it, or an inline
// !important in Index.cshtml, puts a white key back on a dark page.
test('flexui: dark theme, and Classic keys inside it are dark too', async () => {
    const page = await openFlex();
    try {
        const r = await page.eval(`(() => {
            const bg = s => { const e = document.querySelector(s); return e ? getComputedStyle(e).backgroundColor : 'missing'; };
            const fg = s => { const e = document.querySelector(s); return e ? getComputedStyle(e).color : 'missing'; };
            return {
                theme: document.documentElement.dataset.bsTheme,
                body: bg('body'), nav: bg('.iwc-flex-nav'),
                band: bg('.band-radio-label:not(:has(input:checked))'),
                label: fg('.vfo-control-label'), saveMem: fg('#saveMemBtnA'),
            };
        })()`);
        const level = c => { const m = /rgba?\((\d+), (\d+), (\d+)/.exec(c); return m ? (+m[1] + +m[2] + +m[3]) / 3 : NaN; };
        const light = c => level(c) > 180;
        const dark = c => level(c) < 80;
        expect(r.theme === 'dark', `data-bs-theme is ${r.theme}`);
        expect(dark(r.body) && dark(r.nav) && dark(r.band), `light background: ${JSON.stringify(r)}`);
        expect(light(r.label) && light(r.saveMem), `dark text on dark: ${JSON.stringify(r)}`);
    } finally { await page.close(); }
});

// Fabio's bar meters. Each one is fed by MeterPanel mirroring its radial,
// so a bar that is drawn but never moves is the failure to catch.
test('flexui: Linear Meters panel shows, and each bar follows its radial', async () => {
    const page = await openFlex();
    try {
        const r = await page.eval(`(() => {
            const mp = window.meterPanel;
            mp.update('power', 50);
            mp.update('vdd', 13.8);
            updateMeterDomLabel('PowerMeter', { displayValue: { watts: 50, rawAvg: 0 } });
            const tab = [...document.querySelectorAll('.flexlayout__tab_button_content')].map(e => e.textContent.trim());
            return {
                tabs: tab,
                bars: ['power', 'swr', 'compression', 'alc', 'idd', 'vdd'].filter(k => !mp.getGauge(k + 'Linear')),
                width: document.getElementById('powerLinearCanvas')?.width ?? 0,
                power: mp.getGauge('powerLinear')?.gauge.value,
                vdd: mp.getGauge('vddLinear')?.gauge.value,
                chip: document.getElementById('powerLinearValue')?.textContent,
            };
        })()`);
        expect(r.tabs.includes('Linear Meters'), `no Linear Meters tab: ${r.tabs.join(', ')}`);
        expect(r.bars.length === 0, `no bar gauge for: ${r.bars.join(', ')}`);
        expect(r.width > 100, `power bar is ${r.width}px wide`);
        expect(r.power === 50 && r.vdd === 13.8, `bars did not follow: power ${r.power}, vdd ${r.vdd}`);
        expect(r.chip === '50 W', `power chip reads "${r.chip}"`);
    } finally { await page.close(); }
});

// Fabio's VFO cards: the Classic controls moved into his three columns, and
// a bar S-meter that follows the receiving VFO (one receiver on the IC-7300).
test('flexui: VFO cards are three columns, and the S bar follows the receiving VFO', async () => {
    const page = await openFlex();
    try {
        const layout = await page.eval(`(() => {
            const col = (v, c) => document.querySelector('#vfo' + v + 'Col .vfo-col--' + c);
            const has = (v, c, id) => !!col(v, c)?.querySelector('#' + id + v);
            const out = {};
            for (const v of ['A', 'B']) {
                out[v] = {
                    cols: [...document.querySelectorAll('#vfo' + v + 'Col .vfo-body-grid > .vfo-col')].length,
                    tune: ['freq', 'sMeterLinearCanvas', 'rfGainSlider', 'afGainSlider', 'bandRadioGroup', 'segmentSelect', 'modeSelect', 'saveMemBtn'].filter(id => !has(v, 'tune', id)),
                    band: ['ifWidthSelect', 'filGroup', 'ifShapeGroup', 'apfSelect', 'audioFilterBtn', 'rxFilterBtn'].filter(id => !has(v, 'band', id)),
                    keys: ['agcGroup', 'ipoGroup', 'attGroup', 'nrGroup', 'nbGroup', 'notchGroup', 'manualNotchFreq'].filter(id => !has(v, 'keys', id)),
                    leftover: !!document.querySelector('#vfo' + v + 'Col .receiver-controls-group'),
                    status: document.getElementById('vfo' + v + 'StatusLine')?.previousElementSibling?.className,
                    barWidth: document.getElementById('sMeterLinearCanvas' + v)?.width ?? 0,
                };
            }
            return out;
        })()`);
        for (const v of ['A', 'B']) {
            const l = layout[v];
            expect(l.cols === 3, `VFO ${v} has ${l.cols} columns`);
            expect(!l.tune.length && !l.band.length && !l.keys.length, `VFO ${v} misplaced: ${JSON.stringify(l)}`);
            expect(!l.leftover, `VFO ${v} still has the Classic controls group`);
            expect(l.status === 'vfo-body-grid', `VFO ${v} status line is not below the grid (after ${l.status})`);
            expect(l.barWidth > 100, `VFO ${v} S bar is ${l.barWidth}px wide`);
        }

        const read = () => page.eval(`(() => {
            const mp = window.meterPanel;
            const r = v => ({ value: mp.getGauge('smeterBar' + v)?.gauge.value,
                              chip: document.getElementById('sMeterLinearValue' + v)?.textContent,
                              grey: document.getElementById('sMeterLinearRow' + v)?.classList.contains('linear-smeter-inactive') });
            return { A: r('A'), B: r('B') };
        })()`);
        // The 500 ms status poll feeds the meter too (0 here, no radio), so
        // each reading is set and read back in one evaluation.
        const readWith = (raw) => page.eval(`(() => { updateSMeter('A', ${raw}); updateSMeter('B', 0); return 0; })()`).then(read);
        await page.state('ActiveVfo', 0); await sleep(200);
        let r = await readWith(120);
        expect(r.A.value > 0 && !r.A.grey && r.A.chip !== 'S0', `A receiving, A bar: ${JSON.stringify(r.A)}`);
        expect(r.B.value === 0 && r.B.grey && r.B.chip === 'S0', `A receiving, B bar: ${JSON.stringify(r.B)}`);
        // A/B alone moves the greying, before any new reading.
        await page.state('ActiveVfo', 1); await sleep(200);
        r = await read();
        expect(!r.B.grey && r.A.grey, `after A/B the grey bar did not swap: ${JSON.stringify(r)}`);
        r = await readWith(120);
        expect(r.B.value > 0 && !r.B.grey && r.B.chip !== 'S0', `B receiving, B bar: ${JSON.stringify(r.B)}`);
        expect(r.A.value === 0 && r.A.grey && r.A.chip === 'S0', `B receiving, A bar: ${JSON.stringify(r.A)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('flexui: Levels keys follow the radio and write through the Classic controls', async () => {
    const page = await openFlex();
    try {
        expect(await flexWhere(page, 'levels') === 'docked', 'Levels panel is not docked');
        const keys = await page.eval(`[...document.querySelectorAll('#levelsSection .toggle-dd__btn')].length`);
        expect(keys >= 4, `Levels has ${keys} keys, not 4`);
        // Radio -> key, silently.
        await page.state('ProcEnabled', true);
        await page.state('ProcLevel', 42);
        await page.state('MonitorLevelA', 17);
        await sleep(300);
        let s = await page.eval(`({ proc: window.procCycleButton.getState(), mon: window.monitorCycleButton.getState() })`);
        expect(s.proc.selectedId === '1' && s.proc.value === 42, `PROC key did not follow: ${JSON.stringify(s.proc)}`);
        expect(s.mon.value === 17, `MONI key did not follow: ${JSON.stringify(s.mon)}`);
        // Key -> Classic slider (whose change handler does the write).
        await page.eval(`window.powerCycleButton.setState({ value: 30 })`);
        await sleep(400);
        const w = await page.eval(`({ slider: document.getElementById('powerSlider').value, label: document.getElementById('powerValue').textContent })`);
        expect(w.slider === '30' && /30/.test(w.label), `POWER key did not reach the Classic slider: ${JSON.stringify(w)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('flexui: Buttons keys follow the Classic buttons and press them', async () => {
    const page = await openFlex();
    try {
        expect(await flexWhere(page, 'buttons') === 'docked', 'Buttons panel is not docked');
        const k = await page.eval(`(() => {
            const sec = document.getElementById('buttonsSection');
            const strip = document.querySelector('.iwc-flex-toolbar-row');
            return {
                labels: [...sec.querySelectorAll(':scope > .toggle-dd__btn .toggle-dd__label')].map(l => l.textContent),
                leftInStrip: ['atuBtn', 'atuTuneBtn', 'voxBtn', 'voiceBtn', 'dxSpotsOpenBtn'].filter(id => strip?.contains(document.getElementById(id))),
                classicHidden: !!document.getElementById('atuBtn')?.closest('.iwc-flex-buttons-classic[hidden]'),
            };
        })()`);
        const want = ['ATU', 'Tune', 'FM Rep', 'VOX', 'CW', 'CW Read', 'CW Send', 'RTTY', 'DX Spots', 'DX Watch', 'Voice'];
        expect(JSON.stringify(k.labels) === JSON.stringify(want), `keys: ${JSON.stringify(k.labels)}`);
        expect(!k.leftInStrip.length && k.classicHidden, `Classic buttons not parked: ${JSON.stringify(k)}`);
        // Radio -> LEDs. ATU, Tune and VOX are never pressed here: no TX.
        const leds = () => page.eval(`({ atu: document.querySelector('#atuBtnKey .toggle-dd__led').classList.contains('is-on'),
                                         vox: document.querySelector('#voxBtnKey .toggle-dd__led').classList.contains('is-on') })`);
        await page.state('AtuEnabled', true); await page.state('VoxOn', true); await sleep(300);
        let l = await leds();
        expect(l.atu && l.vox, `LEDs did not follow on: ${JSON.stringify(l)}`);
        await page.state('AtuEnabled', false); await page.state('VoxOn', false); await sleep(300);
        l = await leds();
        expect(!l.atu && !l.vox, `LEDs did not follow off: ${JSON.stringify(l)}`);
        // A key presses its Classic button: FM Rep opens the FM dialog.
        const open = await page.eval(`(() => {
            const key = [...document.querySelectorAll('#buttonsSection .toggle-dd__btn')].find(b => b.textContent.trim() === 'FM Rep');
            key.click();
            const d = document.getElementById('fmDialog'); const o = d.open; d.close(); return o;
        })()`);
        expect(open, 'FM Rep key did not open the FM dialog');
        const bad = (await page.writes()).filter(w => FORBIDDEN.test(w.path));
        expect(!bad.length, `transmit writes: ${JSON.stringify(bad)}`);
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

test('flexui: toolbar is a strip above the dock, and VFO Actions is a panel', async () => {
    const page = await openFlex();
    try {
        const t = await page.eval(`(() => {
            const strip = document.querySelector('.iwc-flex-toolbar-row');
            const host = document.getElementById('ywcFlexHost');
            const inStrip = ids => ids.filter(id => !strip?.contains(document.getElementById(id)));
            const actions = document.querySelector('#ywcFlexHost [data-ywc-component="vfoActions"]');
            const vb = document.getElementById('vfoBToggleBtn');
            return {
                strip: !!strip,
                inDock: !!strip?.closest('#ywcFlexHost'),
                above: !!strip && strip.getBoundingClientRect().bottom <= host.getBoundingClientRect().top + 1,
                visible: !!strip && strip.getBoundingClientRect().height > 10,
                missing: inStrip(['utcClock', 'memBtn', 'sMeterHistoryToggleBtn', 'flexPanelsBtn', 'flexLayoutsBtn', 'flexLayoutScale', 'flexResetLayoutBtn', 'connectBtn', 'radioPowerBtn']),
                dockBeforeConnect: !!document.querySelector('.iwc-flex-toolbar-row .iwc-flex-toolbar + #connectBtn'),
                headerEmpty: !document.querySelector('.iwc-flex-nav .iwc-flex-toolbar'),
                notInActions: ['swapVfoBtn', 'copyBtoABtn', 'copyAtoBBtn', 'splitBtn', 'quickSplitBtn'].filter(id => !actions?.contains(document.getElementById(id))),
                vfoBHidden: !vb || getComputedStyle(vb).display === 'none',
            };
        })()`);
        expect(t.strip && !t.inDock, `toolbar strip: ${JSON.stringify(t)}`);
        expect(t.above && t.visible, `the strip is not shown above the dock: ${JSON.stringify(t)}`);
        expect(!t.missing.length, `not in the strip: ${t.missing.join(', ')}`);
        expect(t.dockBeforeConnect && t.headerEmpty, `dock controls not moved in front of Connect: ${JSON.stringify(t)}`);
        expect(!t.notInActions.length, `not in VFO Actions: ${t.notInActions.join(', ')}`);
        expect(t.vfoBHidden, 'the VFO-B show/hide key is still showing (the tab does that here)');
        expect(page.exceptions.length === 0, `uncaught exceptions:\n${page.exceptions.join('\n')}`);
    } finally { await page.close(); }
});

// ----------------------------------------------------------------

try {
    const up = await fetch(BASE + '/About').then(r => r.ok, () => false);
    if (!up) { console.error(`Nothing answering at ${BASE}. Start the app first.`); process.exit(2); }
    browser = await Browser.launch({ headless: !args.includes('--headed') });
    process.exitCode = await suite.run();
} finally {
    browser?.close();
}
