// layout-model.js decides WHICH panels a layout holds; layout-grid.js decides
// where they sit. This file covers the first, plus the store that persists the
// answer, because the two fail together: a reconcile that drops a panel and a
// store that silently returns a half-read document look identical from the
// operator's chair — the panel is gone and nothing said why.
//
// The properties pinned here are the ones with an operator-visible cost:
//
//   1. An essential panel is always in the result. A workspace that can hide
//      everything can hide the route back to the radio, and a stored layout
//      survives the reload that would otherwise fix it.
//   2. A panel the operator deliberately hid stays hidden across reloads;
//      a panel that is genuinely NEW appears. These pull in opposite
//      directions and the difference is the `hidden` list.
//   3. A layout the store cannot read is no layout at all, never a
//      half-guessed one.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"
// (the bare directory form fails on Node 24 under Windows - it tries to load
//  the directory itself as a module.)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    LAYOUT_VERSION, normalisePanel, normaliseCatalogue, defaultLayout,
    reconcile, applyPreset, layoutsEqual
} from '../../js/layout/layout-model.js';
import { createLayoutStore, DEFAULT_LAYOUT_NAME } from '../../js/layout/layout-store.js';

// A catalogue shaped like the one IWC builds from RadioCapabilities: a
// receiver that cannot be switched off, a spectrum panel that only exists when
// an SDR is configured, and the optional extras.
function catalogue(overrides = {}) {
    const entries = [
        { id: 'receiver', title: 'Receiver', group: 'Radio', w: 12, h: 10, minW: 4, minH: 6, essential: true },
        { id: 'meters', title: 'Meters', group: 'Radio', w: 12, h: 4, minW: 3, minH: 3 },
        { id: 'spectrum', title: 'Spectrum', group: 'Spectrum', w: 12, h: 8, minW: 6, minH: 5 },
        { id: 'memories', title: 'Memories', group: 'Operating', w: 6, h: 6, minW: 3, minH: 4 },
        { id: 'cw-keyer', title: 'CW Keyer', group: 'Operating', w: 6, h: 5, minW: 3, minH: 3 }
    ];
    return entries.map(e => (e.id in overrides ? { ...e, ...overrides[e.id] } : e));
}

/** A Map behind the localStorage interface, so the store can be driven here. */
function fakeStorage(seed = {}) {
    const map = new Map(Object.entries(seed));
    return {
        get length() { return map.size; },
        key(i) { return [...map.keys()][i] ?? null; },
        getItem(k) { return map.has(k) ? map.get(k) : null; },
        setItem(k, v) { map.set(k, String(v)); },
        removeItem(k) { map.delete(k); },
        _map: map
    };
}

/** A storage that throws on everything — private browsing, blocked site data. */
function hostileStorage() {
    const boom = () => { throw new DOMException('The operation is insecure.'); };
    return {
        get length() { return boom(); },
        key: boom, getItem: boom, setItem: boom, removeItem: boom
    };
}

function ids(layout) { return layout.panels.map(p => p.id); }

/* ------------------------------------------------------------------ *
 * normalisePanel / normaliseCatalogue
 * ------------------------------------------------------------------ */

test('normalisePanel fills in what the application left out', () => {
    const p = normalisePanel({ id: 'x' });
    assert.equal(p.title, 'x', 'a missing title falls back to the id, not to blank');
    assert.equal(p.group, 'Panels');
    assert.equal(p.w, 1);
    assert.equal(p.h, 1);
    assert.equal(p.essential, false);
    assert.equal(p.available, true, 'a forgotten available flag must mean shown');
});

test('normalisePanel rejects an entry that cannot be addressed', () => {
    // An id is what gets persisted, so a panel without one cannot be saved,
    // restored or switched off — better dropped at the door than half-alive.
    assert.equal(normalisePanel({ title: 'No id' }), null);
    assert.equal(normalisePanel({ id: '   ' }), null);
    assert.equal(normalisePanel(null), null);
});

test('normalisePanel never lets a default size fall below its own minimum', () => {
    const p = normalisePanel({ id: 'x', w: 2, h: 1, minW: 5, minH: 4 });
    assert.equal(p.w, 5);
    assert.equal(p.h, 4);
});

test('normaliseCatalogue drops duplicate ids, keeping the first', () => {
    const out = normaliseCatalogue([
        { id: 'a', title: 'First' }, { id: 'a', title: 'Second' }, { id: 'b' }
    ]);
    assert.deepEqual(out.map(p => p.id), ['a', 'b']);
    assert.equal(out[0].title, 'First');
});

/* ------------------------------------------------------------------ *
 * defaultLayout
 * ------------------------------------------------------------------ */

test('defaultLayout lays panels out at catalogue size, in catalogue order', () => {
    const layout = defaultLayout(normaliseCatalogue(catalogue()), 12);
    assert.equal(layout.version, LAYOUT_VERSION);
    // Catalogue order and catalogue size on purpose: the application chose
    // those to mirror the page the operator already has, so the first thing
    // they see when they switch to Workspace is recognisable, not a puzzle.
    assert.deepEqual(ids(layout), ['receiver', 'meters', 'spectrum', 'memories', 'cw-keyer']);
    assert.deepEqual(layout.panels.map(p => [p.col, p.row, p.w, p.h]), [
        [0, 0, 12, 10],   // receiver
        [0, 10, 12, 4],   // meters
        [0, 14, 12, 8],   // spectrum
        [0, 22, 6, 6],    // memories  — the two half-width panels sit
        [6, 22, 6, 5],    //  cw-keyer    beside each other, not stacked
    ]);
});

test('defaultLayout wraps to a new row below the tallest panel on the last one', () => {
    // Two receivers at w:6 belong beside each other — that is the whole
    // reason the catalogue says 6 and not 12 — and the next full-width panel
    // drops below the taller of the pair, not the shorter.
    const cat = normaliseCatalogue([
        { id: 'vfo-a', title: 'VFO A', group: 'Radio', w: 6, h: 10, minW: 4, minH: 6 },
        { id: 'vfo-b', title: 'VFO B', group: 'Radio', w: 6, h: 12, minW: 4, minH: 6 },
        { id: 'meters', title: 'Meters', group: 'Radio', w: 12, h: 4, minW: 3, minH: 3 },
    ]);
    const layout = defaultLayout(cat, 12);
    assert.deepEqual(layout.panels.map(p => [p.col, p.row, p.w]), [[0, 0, 6], [6, 0, 6], [0, 12, 12]]);

    // A one-column grid (a phone) cannot put anything beside anything, and
    // must stack in order like the classic page's collapsed columns.
    const narrow = defaultLayout(cat, 1);
    assert.deepEqual(narrow.panels.map(p => [p.col, p.row, p.w]), [[0, 0, 1], [0, 10, 1], [0, 22, 1]]);
});

test('defaultLayout omits what this station cannot show', () => {
    const layout = defaultLayout(
        normaliseCatalogue(catalogue({ spectrum: { available: false } })), 12);
    assert.equal(ids(layout).includes('spectrum'), false);
});

/* ------------------------------------------------------------------ *
 * reconcile — the function that has to cope with reality
 * ------------------------------------------------------------------ */

test('reconcile keeps a stored arrangement intact', () => {
    const cat = normaliseCatalogue(catalogue());
    const stored = {
        version: LAYOUT_VERSION, columns: 12, panels: [
            { id: 'receiver', col: 0, row: 0, w: 6, h: 10 },
            { id: 'spectrum', col: 6, row: 0, w: 6, h: 8 },
            { id: 'meters', col: 0, row: 10, w: 12, h: 4 },
            { id: 'memories', col: 0, row: 14, w: 6, h: 6 },
            { id: 'cw-keyer', col: 6, row: 14, w: 6, h: 5 }
        ], hidden: []
    };
    const out = reconcile(stored, cat, 12);
    assert.ok(layoutsEqual(out, stored), 'a complete stored layout must come back unchanged');
});

test('reconcile drops a panel this station no longer has', () => {
    // The operator unplugged the SDR. The spectrum panel cannot render, and a
    // panel shell with nothing in it reads as a broken application.
    const cat = normaliseCatalogue(catalogue({ spectrum: { available: false } }));
    const stored = {
        version: LAYOUT_VERSION, panels: [
            { id: 'receiver', col: 0, row: 0, w: 12, h: 10 },
            { id: 'spectrum', col: 0, row: 10, w: 12, h: 8 }
        ]
    };
    const out = reconcile(stored, cat, 12);
    assert.equal(ids(out).includes('spectrum'), false);
    assert.equal(ids(out).includes('receiver'), true);
});

test('reconcile appends a panel that shipped after the layout was saved', () => {
    // A feature the operator cannot find is a feature they will report as
    // missing, so a genuinely new panel appears rather than staying dark.
    const cat = normaliseCatalogue(catalogue());
    const stored = {
        version: LAYOUT_VERSION, panels: [
            { id: 'receiver', col: 0, row: 0, w: 12, h: 10 }
        ], hidden: ['meters', 'spectrum', 'memories']
    };
    const out = reconcile(stored, cat, 12);
    assert.equal(ids(out).includes('cw-keyer'), true, 'never seen before -> appears');
    assert.equal(ids(out).includes('meters'), false, 'deliberately hidden -> stays hidden');
    assert.deepEqual(out.hidden.sort(), ['memories', 'meters', 'spectrum']);
});

test('reconcile restores an essential panel a stored layout has lost', () => {
    // The escape route. However the stored layout came to be missing the
    // receiver — a corrupt write, a hand edit, a bug that has since been
    // fixed — the operator must not have to clear site data to get it back.
    const cat = normaliseCatalogue(catalogue());
    const stored = {
        version: LAYOUT_VERSION,
        panels: [{ id: 'meters', col: 0, row: 0, w: 12, h: 4 }],
        hidden: ['receiver', 'spectrum', 'memories', 'cw-keyer']
    };
    const out = reconcile(stored, cat, 12);
    assert.equal(ids(out).includes('receiver'), true);
    assert.equal(out.hidden.includes('receiver'), false,
        'an essential panel can never be recorded as hidden');
});

test('reconcile survives an empty or corrupt stored layout', () => {
    const cat = normaliseCatalogue(catalogue());
    for (const stored of [null, {}, { panels: null }, { panels: [{}, { id: 42 }] }]) {
        const out = reconcile(stored, cat, 12);
        assert.equal(ids(out).includes('receiver'), true,
            'whatever came back, the operator gets their receiver');
        assert.equal(out.version, LAYOUT_VERSION);
    }
});

test('reconcile respects each panel\'s minimum size', () => {
    const cat = normaliseCatalogue(catalogue());
    const stored = {
        version: LAYOUT_VERSION,
        panels: [{ id: 'spectrum', col: 0, row: 0, w: 1, h: 1 }]
    };
    const spectrum = reconcile(stored, cat, 12).panels.find(p => p.id === 'spectrum');
    assert.equal(spectrum.w, 6, 'minW');
    assert.equal(spectrum.h, 5, 'minH');
});

test('reconcile does not resurrect a hidden panel when one is unplugged and replugged', () => {
    // Hide the memories panel, unplug the SDR (spectrum leaves the catalogue),
    // plug it back in. The memories panel must still be hidden, and the
    // spectrum panel — which was never hidden, only absent — must come back.
    const full = normaliseCatalogue(catalogue());
    const noSdr = normaliseCatalogue(catalogue({ spectrum: { available: false } }));

    let layout = defaultLayout(full, 12);
    layout = { ...layout, panels: layout.panels.filter(p => p.id !== 'memories'), hidden: ['memories'] };

    const unplugged = reconcile(layout, noSdr, 12);
    assert.equal(ids(unplugged).includes('spectrum'), false);
    assert.equal(unplugged.hidden.includes('spectrum'), false,
        'absent is not the same as hidden');

    const replugged = reconcile(unplugged, full, 12);
    assert.equal(ids(replugged).includes('spectrum'), true, 'comes back on replug');
    assert.equal(ids(replugged).includes('memories'), false, 'stays hidden across the round trip');
});

/* ------------------------------------------------------------------ *
 * applyPreset
 * ------------------------------------------------------------------ */

test('applyPreset places only the panels it names', () => {
    const cat = normaliseCatalogue(catalogue());
    const out = applyPreset({ show: ['receiver', 'meters'], wide: ['receiver'] }, cat, 12);
    assert.deepEqual(ids(out).sort(), ['meters', 'receiver']);
    assert.deepEqual(out.hidden.sort(), ['cw-keyer', 'memories', 'spectrum']);
});

test('applyPreset adds an essential panel the preset forgot', () => {
    // A preset is a convenience, not a way to lock the operator out.
    const cat = normaliseCatalogue(catalogue());
    const out = applyPreset({ show: ['meters', 'cw-keyer'] }, cat, 12);
    assert.equal(ids(out).includes('receiver'), true);
});

test('applyPreset ignores a panel this station does not have', () => {
    const cat = normaliseCatalogue(catalogue({ spectrum: { available: false } }));
    const out = applyPreset({ show: ['receiver', 'spectrum', 'meters'] }, cat, 12);
    assert.equal(ids(out).includes('spectrum'), false);
});

test('applyPreset works at every column count', () => {
    // The same preset has to make sense on a 12-column monitor and a
    // 1-column phone, which is why a preset names panels and not positions.
    const cat = normaliseCatalogue(catalogue());
    const preset = { show: ['receiver', 'spectrum', 'meters', 'memories', 'cw-keyer'], wide: ['spectrum'] };
    for (const cols of [12, 6, 1]) {
        const out = applyPreset(preset, cat, cols);
        assert.equal(out.panels.length, 5, `${cols} columns: every named panel must be placed`);
        for (const p of out.panels) {
            assert.ok(p.col >= 0 && p.col + p.w <= cols,
                `${cols} columns: ${p.id} at ${p.col}+${p.w} is outside the grid`);
        }
    }
});

test('applyPreset pairs panels side by side when there is room', () => {
    const cat = normaliseCatalogue(catalogue());
    const out = applyPreset({ show: ['receiver', 'memories', 'cw-keyer'], wide: ['receiver'] }, cat, 12);
    const mem = out.panels.find(p => p.id === 'memories');
    const cw = out.panels.find(p => p.id === 'cw-keyer');
    assert.equal(mem.row, cw.row, 'both fit on one row at 12 columns');
    assert.notEqual(mem.col, cw.col);
});

/* ------------------------------------------------------------------ *
 * layoutsEqual
 * ------------------------------------------------------------------ */

test('layoutsEqual ignores order but not position', () => {
    const a = { panels: [{ id: 'x', col: 0, row: 0, w: 6, h: 4 }, { id: 'y', col: 6, row: 0, w: 6, h: 4 }] };
    const b = { panels: [{ id: 'y', col: 6, row: 0, w: 6, h: 4 }, { id: 'x', col: 0, row: 0, w: 6, h: 4 }] };
    assert.equal(layoutsEqual(a, b), true);
    const moved = { panels: [{ id: 'x', col: 0, row: 1, w: 6, h: 4 }, { id: 'y', col: 6, row: 0, w: 6, h: 4 }] };
    assert.equal(layoutsEqual(a, moved), false);
    assert.equal(layoutsEqual(a, { panels: [] }), false);
    assert.equal(layoutsEqual(null, null), true);
});

/* ------------------------------------------------------------------ *
 * layout-store
 * ------------------------------------------------------------------ */

test('the store round-trips a layout', () => {
    const storage = fakeStorage();
    const store = createLayoutStore('iwc', storage);
    const layout = defaultLayout(normaliseCatalogue(catalogue()), 12);

    assert.equal(store.save(DEFAULT_LAYOUT_NAME, layout), true);
    const back = store.load(DEFAULT_LAYOUT_NAME);
    assert.ok(layoutsEqual(back, layout));
    assert.equal(back.version, LAYOUT_VERSION);
    assert.ok(back.savedAt, 'a saved layout records when, for anyone diagnosing one later');
});

test('the store keeps each application in its own namespace', () => {
    // Both applications can be open on one machine, on the same host and port
    // history, and neither may read the other's layouts — the panel ids do not
    // even mean the same thing.
    const storage = fakeStorage();
    const iwc = createLayoutStore('iwc', storage);
    const ywc = createLayoutStore('ywc', storage);
    iwc.save('default', { panels: [{ id: 'receiver', col: 0, row: 0, w: 12, h: 6 }] });
    assert.equal(ywc.load('default'), null);
    assert.ok(iwc.load('default'));
});

test('the store separates which layout is current from what it holds', () => {
    const store = createLayoutStore('iwc', fakeStorage());
    assert.equal(store.currentName(), DEFAULT_LAYOUT_NAME);
    store.save('contest', { panels: [] });
    store.setCurrentName('contest');
    assert.equal(store.currentName(), 'contest');
    // Switching layouts must not rewrite the one being left behind.
    assert.deepEqual(store.names(), ['contest']);
});

test('the store lists saved names without the current-layout marker', () => {
    const store = createLayoutStore('iwc', fakeStorage());
    store.save('default', { panels: [] });
    store.save('contest', { panels: [] });
    store.setCurrentName('contest');
    assert.deepEqual(store.names(), ['contest', 'default'],
        '"current" is bookkeeping, not a layout the operator can pick');
});

test('the store refuses to delete the default layout', () => {
    const store = createLayoutStore('iwc', fakeStorage());
    store.save(DEFAULT_LAYOUT_NAME, { panels: [] });
    assert.equal(store.remove(DEFAULT_LAYOUT_NAME), false);
    assert.ok(store.load(DEFAULT_LAYOUT_NAME));
    store.save('contest', { panels: [] });
    assert.equal(store.remove('contest'), true);
    assert.equal(store.load('contest'), null);
});

test('the store returns null for anything it cannot read', () => {
    const storage = fakeStorage({
        'iwc.layout.torn': '{"panels":[{"id":"a"',        // a truncated write
        'iwc.layout.wrong': '{"version":1,"panels":"no"}', // panels not an array
        'iwc.layout.empty': ''
    });
    const store = createLayoutStore('iwc', storage);
    assert.equal(store.load('torn'), null);
    assert.equal(store.load('wrong'), null);
    assert.equal(store.load('empty'), null);
    assert.equal(store.load('never-saved'), null);
});

test('the store refuses a layout from a future version', () => {
    // Losing the arrangement is bad; rendering a misread one loses the radio,
    // which is worse and much harder to explain.
    const storage = fakeStorage({
        'iwc.layout.default': JSON.stringify({ version: LAYOUT_VERSION + 1, panels: [] })
    });
    assert.equal(createLayoutStore('iwc', storage).load('default'), null);
});

test('the store reads a layout from an older version', () => {
    const storage = fakeStorage({
        'iwc.layout.default': JSON.stringify({
            version: 0, panels: [{ id: 'receiver', col: 0, row: 0, w: 12, h: 6 }]
        })
    });
    const back = createLayoutStore('iwc', storage).load('default');
    assert.ok(back, 'an older layout is migrated, not discarded');
    assert.equal(back.version, LAYOUT_VERSION);
});

test('the store degrades to "no stored layout" when storage throws', () => {
    // Private browsing, blocked site data and a full quota all throw rather
    // than returning null. An exception here would be thrown while restoring
    // the layout on load — that is, it would take the whole page down.
    const store = createLayoutStore('iwc', hostileStorage());
    assert.equal(store.load('default'), null);
    assert.equal(store.save('default', { panels: [] }), false);
    assert.deepEqual(store.names(), []);
    assert.equal(store.currentName(), DEFAULT_LAYOUT_NAME);
    assert.equal(store.setCurrentName('contest'), false);
});

test('a store with no storage at all still answers', () => {
    // Server-side rendering, a test harness, an embedded view with storage
    // disabled: `createLayoutStore(prefix)` must not throw on construction.
    const store = createLayoutStore('iwc', null);
    if (typeof localStorage === 'undefined') {
        assert.equal(store.load('default'), null);
        assert.equal(store.currentName(), DEFAULT_LAYOUT_NAME);
    }
});

/* ------------------------------------------------------------------ *
 * The two halves together
 * ------------------------------------------------------------------ */

test('a layout survives save, reload and reconcile unchanged', () => {
    // The whole point of the feature: the operator arranges their screen once
    // and finds it the same way tomorrow.
    const cat = normaliseCatalogue(catalogue());
    const storage = fakeStorage();
    const store = createLayoutStore('iwc', storage);

    const arranged = {
        version: LAYOUT_VERSION, columns: 12, hidden: ['cw-keyer'], panels: [
            { id: 'spectrum', col: 0, row: 0, w: 12, h: 8 },
            { id: 'receiver', col: 0, row: 8, w: 8, h: 10 },
            { id: 'memories', col: 8, row: 8, w: 4, h: 6 },
            { id: 'meters', col: 8, row: 14, w: 4, h: 4 }
        ]
    };
    store.save('default', arranged);

    const reloaded = reconcile(store.load('default'), cat, 12);
    assert.ok(layoutsEqual(reloaded, arranged), 'the arrangement came back as it was left');
    assert.equal(ids(reloaded).includes('cw-keyer'), false, 'and the hidden panel stayed hidden');
});
