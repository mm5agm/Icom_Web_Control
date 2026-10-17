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
const radioPage = async () => {
    const page = await open('/radio', { settleMs: 2000 });
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

test('/radio: M.STORE saves the selected VFO to IWC memories', async () => {
    const page = await radioPage();
    try {
        await clickButton(page, '.rp-mem-keys', 'M.STORE');
        const w = writesTo(await page.writes(), '/api/memory/save-vfo/a');
        expect(w.length === 1, `M.STORE wrote ${w.length}`);
        await page.waitFor(`[...document.querySelectorAll('.rp-mem-keys .rp-sub')].some(s => s.textContent === 'SAVED')`, { what: 'SAVED on M.STORE' });
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
        for (const c of ['toolbar', 'meters', 'spectrum', 'vfoA', 'vfoB']) {
            expect(await flexWhere(page, c) === 'docked', `${c} is ${await flexWhere(page, c)}, not docked`);
        }
        expect(await flexWhere(page, 'clarifier') === 'docked', 'clarifier is not in the dock (it is a tab behind Toolbar)');
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

// ----------------------------------------------------------------

try {
    const up = await fetch(BASE + '/About').then(r => r.ok, () => false);
    if (!up) { console.error(`Nothing answering at ${BASE}. Start the app first.`); process.exit(2); }
    browser = await Browser.launch({ headless: !args.includes('--headed') });
    process.exitCode = await suite.run();
} finally {
    browser?.close();
}
