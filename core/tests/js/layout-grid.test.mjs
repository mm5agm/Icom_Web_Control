// layout-grid.js is the only part of the workspace that can be tested without
// a browser, which is exactly why the fiddly reasoning was put there. Dragging
// a panel with a mouse is not testable in `node --test`; deciding where the
// panel lands is, and that decision is what an operator notices when it is
// wrong.
//
// Three properties are pinned here, and they are the ones that break silently:
//
//   1. Panels never overlap after any operation. An overlap does not throw and
//      does not look like a bug in a screenshot — it looks like a panel that
//      "went missing", because the other one is painted on top of it.
//   2. Displacement is downward only. Horizontal position is muscle memory:
//      the band buttons are on the left of the receiver panel and always have
//      been. A sideways shuffle moves panels the operator never touched.
//   3. Nothing hangs. Every settle loop has a guard, and a grid that locks up
//      takes the radio's whole user interface with it.
//
// Run from the core repo root:  node --test "tests/js/*.test.mjs"
// (the bare directory form fails on Node 24 under Windows - it tries to load
//  the directory itself as a module.)

import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
    MIN_W, MIN_H, normaliseRect, overlaps, readingOrder, resolveCollisions,
    compact, movePanel, resizePanel, reflow, findSlot, gridRows
} from '../../js/layout/layout-grid.js';

/** Every pair of panels, so a test can assert on all of them at once. */
function pairs(panels) {
    const out = [];
    for (let i = 0; i < panels.length; i++) {
        for (let j = i + 1; j < panels.length; j++) { out.push([panels[i], panels[j]]); }
    }
    return out;
}

function assertNoOverlaps(panels, what) {
    for (const [a, b] of pairs(panels)) {
        assert.equal(overlaps(a, b), false,
            `${what}: ${a.id} (${a.col},${a.row} ${a.w}x${a.h}) overlaps ` +
            `${b.id} (${b.col},${b.row} ${b.w}x${b.h})`);
    }
}

function byId(panels, id) {
    const p = panels.find(x => x.id === id);
    assert.ok(p, `expected a panel with id ${id}`);
    return p;
}

// A stack of three full-width panels in a 12-column grid: the shape both
// applications start from, because defaultLayout() builds exactly this.
function stack() {
    return [
        { id: 'a', col: 0, row: 0, w: 12, h: 4 },
        { id: 'b', col: 0, row: 4, w: 12, h: 6 },
        { id: 'c', col: 0, row: 10, w: 12, h: 3 }
    ];
}

/* ------------------------------------------------------------------ *
 * normaliseRect — the one function every untrusted rect passes through
 * ------------------------------------------------------------------ */

test('normaliseRect clamps a rect into the grid', () => {
    assert.deepEqual(normaliseRect({ col: 0, row: 0, w: 6, h: 4 }, 12),
        { col: 0, row: 0, w: 6, h: 4 });
    // Too wide for the grid: narrowed, not dropped. A panel the operator
    // cannot see is indistinguishable from one the app has lost.
    assert.deepEqual(normaliseRect({ col: 0, row: 0, w: 20, h: 2 }, 12),
        { col: 0, row: 0, w: 12, h: 2 });
    // Pushed back inside when it would hang off the right edge.
    assert.deepEqual(normaliseRect({ col: 10, row: 0, w: 6, h: 2 }, 12),
        { col: 6, row: 0, w: 6, h: 2 });
});

test('normaliseRect does width before column', () => {
    // This is the ordering bug the implementation comment warns about. A
    // 6-wide panel at column 11 of a 12-column grid must keep its width and
    // move left, not stay put and shrink to 1. Clamping the column first
    // would pin it at 11 and leave only one column of room.
    const r = normaliseRect({ col: 11, row: 0, w: 6, h: 2 }, 12);
    assert.equal(r.w, 6);
    assert.equal(r.col, 6);
});

test('normaliseRect survives a hand-edited localStorage value', () => {
    // Not hypothetical: the layout is stored as JSON under a key an operator
    // can see and edit in devtools. Whatever comes back must still render.
    const junk = normaliseRect({ col: -5, row: -2, w: 0, h: NaN }, 12);
    assert.deepEqual(junk, { col: 0, row: 0, w: MIN_W, h: MIN_H });
    assert.deepEqual(normaliseRect(null, 12), { col: 0, row: 0, w: 1, h: 1 });
    assert.deepEqual(normaliseRect(undefined, 0), { col: 0, row: 0, w: 1, h: 1 });
    // Fractional values, which is what a bad pointer-maths change would emit.
    assert.deepEqual(normaliseRect({ col: 2.7, row: 1.9, w: 3.4, h: 2.6 }, 12),
        { col: 2, row: 1, w: 3, h: 2 });
});

/* ------------------------------------------------------------------ *
 * overlaps and readingOrder
 * ------------------------------------------------------------------ */

test('overlaps is true only when a cell is actually shared', () => {
    const a = { col: 0, row: 0, w: 4, h: 4 };
    assert.equal(overlaps(a, { col: 3, row: 3, w: 2, h: 2 }), true);
    // Touching edges are not an overlap — a panel ending at row 4 and one
    // starting at row 4 are adjacent, and getting this off by one would make
    // every stacked layout push itself apart on load.
    assert.equal(overlaps(a, { col: 4, row: 0, w: 2, h: 2 }), false);
    assert.equal(overlaps(a, { col: 0, row: 4, w: 2, h: 2 }), false);
});

test('readingOrder is top to bottom, then left to right', () => {
    const sorted = [
        { id: 'x', col: 6, row: 0 }, { id: 'y', col: 0, row: 0 },
        { id: 'z', col: 0, row: 3 }
    ].sort(readingOrder).map(p => p.id);
    assert.deepEqual(sorted, ['y', 'x', 'z']);
});

test('readingOrder is stable for two panels in the same cell', () => {
    // Only reachable from a corrupt stored layout, but a comparator that
    // returns 0 for distinct panels makes the sort order depend on the
    // engine's sort implementation, and then a layout renders differently in
    // two browsers for no visible reason.
    const a = { id: 'a', col: 0, row: 0 };
    const b = { id: 'b', col: 0, row: 0 };
    assert.ok(readingOrder(a, b) < 0);
    assert.ok(readingOrder(b, a) > 0);
});

/* ------------------------------------------------------------------ *
 * resolveCollisions — downward only, and the anchor never moves
 * ------------------------------------------------------------------ */

test('resolveCollisions pushes the collided panel down, not sideways', () => {
    const panels = [
        { id: 'a', col: 0, row: 0, w: 6, h: 4 },
        { id: 'b', col: 0, row: 0, w: 6, h: 4 }   // dropped straight on top
    ];
    const out = resolveCollisions(panels, 'a');
    assertNoOverlaps(out, 'after resolve');
    assert.deepEqual(byId(out, 'a'), { id: 'a', col: 0, row: 0, w: 6, h: 4 });
    const b = byId(out, 'b');
    assert.equal(b.col, 0, 'b must keep its column');
    assert.equal(b.row, 4, 'b must be pushed below a');
});

test('resolveCollisions never changes any column', () => {
    const panels = [
        { id: 'a', col: 0, row: 0, w: 8, h: 4 },
        { id: 'b', col: 4, row: 2, w: 8, h: 4 },
        { id: 'c', col: 2, row: 1, w: 4, h: 6 }
    ];
    const before = new Map(panels.map(p => [p.id, p.col]));
    const out = resolveCollisions(panels, 'a');
    for (const p of out) {
        assert.equal(p.col, before.get(p.id), `${p.id} moved sideways`);
    }
    assertNoOverlaps(out, 'three-way');
});

test('resolveCollisions cascades', () => {
    // a is dragged onto b, b must clear a, and c must then clear b.
    const panels = [
        { id: 'a', col: 0, row: 0, w: 12, h: 5 },
        { id: 'b', col: 0, row: 0, w: 12, h: 3 },
        { id: 'c', col: 0, row: 3, w: 12, h: 3 }
    ];
    const out = resolveCollisions(panels, 'a');
    assertNoOverlaps(out, 'cascade');
    assert.equal(byId(out, 'a').row, 0);
    assert.equal(byId(out, 'b').row, 5);
    assert.equal(byId(out, 'c').row, 8);
});

test('resolveCollisions does not modify its input', () => {
    // The engine renders from the array it is handed; mutating in place makes
    // a failed operation unrecoverable and the bug looks like "the layout
    // jumped" rather than "resolveCollisions is impure".
    const panels = stack();
    const snapshot = JSON.stringify(panels);
    resolveCollisions(panels, 'b');
    assert.equal(JSON.stringify(panels), snapshot);
});

test('resolveCollisions terminates on a fully-overlapping pile', () => {
    // Twenty panels all in the same cell — the worst case a corrupt stored
    // layout can produce. The point is that this returns at all.
    const pile = Array.from({ length: 20 }, (_, i) =>
        ({ id: `p${i}`, col: 0, row: 0, w: 12, h: 2 }));
    const out = resolveCollisions(pile, 'p0');
    assert.equal(out.length, 20);
    assertNoOverlaps(out, 'pile');
});

/* ------------------------------------------------------------------ *
 * compact — the pass that stops empty bands accumulating
 * ------------------------------------------------------------------ */

test('compact pulls panels up into free space', () => {
    const out = compact([
        { id: 'a', col: 0, row: 5, w: 12, h: 2 },
        { id: 'b', col: 0, row: 9, w: 12, h: 2 }
    ]);
    assert.equal(byId(out, 'a').row, 0);
    assert.equal(byId(out, 'b').row, 2);
});

test('compact does not pull a panel through one beside it', () => {
    // Side by side at row 4 with nothing above: both rise to 0 and neither
    // ends up on top of the other.
    const out = compact([
        { id: 'a', col: 0, row: 4, w: 6, h: 3 },
        { id: 'b', col: 6, row: 4, w: 6, h: 3 }
    ]);
    assert.equal(byId(out, 'a').row, 0);
    assert.equal(byId(out, 'b').row, 0);
    assertNoOverlaps(out, 'side by side');
});

test('compact leaves an already-tight layout alone', () => {
    // Idempotence matters because compact runs after every move: if it moved
    // things on a second pass the layout would drift while the operator was
    // doing nothing.
    const once = compact(stack());
    const twice = compact(once);
    assert.deepEqual(twice, once);
});

/* ------------------------------------------------------------------ *
 * movePanel
 * ------------------------------------------------------------------ */

test('movePanel puts the panel where it was asked and settles the rest', () => {
    const out = movePanel(stack(), 'c', 0, 0, 12);
    assertNoOverlaps(out, 'after move');
    assert.equal(byId(out, 'c').row, 0, 'the dragged panel wins its position');
    assert.equal(byId(out, 'a').row, 3);
    assert.equal(byId(out, 'b').row, 7);
});

test('movePanel keeps the panel inside the grid', () => {
    const out = movePanel(stack(), 'a', 99, -4, 12);
    const a = byId(out, 'a');
    assert.equal(a.row, 0);
    assert.equal(a.col, 0);       // 12 wide can only sit at column 0
    assert.equal(a.w, 12);
});

test('movePanel on an unknown id changes nothing but the order', () => {
    const out = movePanel(stack(), 'nope', 0, 0, 12);
    assert.deepEqual(out.map(p => p.id), ['a', 'b', 'c']);
    assertNoOverlaps(out, 'unknown id');
});

test('movePanel returns panels in reading order', () => {
    // The DOM engine appends shells in array order, and the tab sequence is
    // therefore this order. A layout whose array order does not match what is
    // on screen tabs around the page at random.
    const out = movePanel(stack(), 'c', 0, 0, 12);
    const ids = out.map(p => p.id);
    assert.deepEqual(ids, ['c', 'a', 'b']);
});

/* ------------------------------------------------------------------ *
 * resizePanel
 * ------------------------------------------------------------------ */

test('resizePanel is anchored at the top-left', () => {
    const panels = [{ id: 'a', col: 3, row: 2, w: 4, h: 4 }];
    const out = resizePanel(panels, 'a', 6, 6, 12);
    const a = byId(out, 'a');
    assert.equal(a.col, 3, 'the corner the operator is not dragging must not move');
    assert.equal(a.w, 6);
    assert.equal(a.h, 6);
});

test('resizePanel clips at the right edge instead of sliding left', () => {
    // Sliding would move the panel the operator is dragging the opposite edge
    // of, which reads as the application fighting them.
    const panels = [{ id: 'a', col: 8, row: 0, w: 4, h: 2 }];
    const out = resizePanel(panels, 'a', 10, 2, 12);
    const a = byId(out, 'a');
    assert.equal(a.col, 8);
    assert.equal(a.w, 4, 'only four columns remain to the right of column 8');
});

test('resizePanel will not go below one cell', () => {
    const out = resizePanel([{ id: 'a', col: 0, row: 0, w: 4, h: 4 }], 'a', -3, 0, 12);
    const a = byId(out, 'a');
    assert.equal(a.w, MIN_W);
    assert.equal(a.h, MIN_H);
});

test('resizePanel pushes what it grows into downward', () => {
    const out = resizePanel(stack(), 'a', 12, 8, 12);
    assertNoOverlaps(out, 'after grow');
    assert.equal(byId(out, 'a').h, 8);
    assert.equal(byId(out, 'b').row, 8);
});

/* ------------------------------------------------------------------ *
 * reflow — the wide monitor's layout arriving on the bench tablet
 * ------------------------------------------------------------------ */

test('reflow narrows panels rather than dropping them', () => {
    const wide = [
        { id: 'a', col: 0, row: 0, w: 6, h: 3 },
        { id: 'b', col: 6, row: 0, w: 6, h: 3 }
    ];
    const out = reflow(wide, 1);
    assert.equal(out.length, 2, 'no panel may be lost');
    for (const p of out) { assert.equal(p.w, 1); assert.equal(p.col, 0); }
    assertNoOverlaps(out, 'one column');
});

test('reflow to one column stacks in reading order', () => {
    const out = reflow([
        { id: 'right', col: 6, row: 0, w: 6, h: 2 },
        { id: 'left', col: 0, row: 0, w: 6, h: 2 },
        { id: 'below', col: 0, row: 2, w: 12, h: 2 }
    ], 1);
    assert.deepEqual(out.map(p => p.id), ['left', 'right', 'below']);
    assert.deepEqual(out.map(p => p.row), [0, 2, 4]);
});

test('reflow is idempotent at the same column count', () => {
    const once = reflow(stack(), 6);
    assert.deepEqual(reflow(once, 6), once);
});

test('reflow of an empty layout is an empty layout', () => {
    assert.deepEqual(reflow([], 12), []);
    assert.equal(gridRows([]), 0);
});

/* ------------------------------------------------------------------ *
 * findSlot — switching a panel on from the rail
 * ------------------------------------------------------------------ */

test('findSlot uses a gap when there is one', () => {
    // A 6-wide panel at column 0 leaves columns 6-11 of rows 0-3 free.
    const panels = [{ id: 'a', col: 0, row: 0, w: 6, h: 4 }];
    const slot = findSlot(panels, 6, 4, 12);
    assert.deepEqual(slot, { col: 6, row: 0, w: 6, h: 4 });
});

test('findSlot falls back to the bottom when nothing fits', () => {
    const panels = [{ id: 'a', col: 0, row: 0, w: 12, h: 4 }];
    const slot = findSlot(panels, 12, 3, 12);
    assert.equal(slot.row, 4);
    assert.equal(slot.col, 0);
});

test('findSlot never returns a slot that overlaps something', () => {
    // The fallback branch is the one worth pinning: it is reached from the
    // rail, so a wrong answer there paints a newly-shown panel on top of one
    // the operator was already using.
    const panels = [
        { id: 'a', col: 0, row: 0, w: 7, h: 4 },
        { id: 'b', col: 7, row: 0, w: 5, h: 9 },
        { id: 'c', col: 0, row: 4, w: 7, h: 5 }
    ];
    for (const [w, h] of [[1, 1], [4, 2], [8, 3], [12, 6]]) {
        const slot = findSlot(panels, w, h, 12);
        for (const p of panels) {
            assert.equal(overlaps(p, slot), false,
                `slot ${w}x${h} at (${slot.col},${slot.row}) overlaps ${p.id}`);
        }
        assert.ok(slot.col + slot.w <= 12, 'slot must be inside the grid');
    }
});

test('findSlot clamps a panel wider than the grid', () => {
    const slot = findSlot([], 20, 2, 6);
    assert.equal(slot.w, 6);
    assert.equal(slot.col, 0);
});

/* ------------------------------------------------------------------ *
 * gridRows
 * ------------------------------------------------------------------ */

test('gridRows is the bottom of the lowest panel', () => {
    assert.equal(gridRows(stack()), 13);
    assert.equal(gridRows([{ id: 'a', col: 0, row: 7, w: 1, h: 2 }]), 9);
});

/* ------------------------------------------------------------------ *
 * The property that matters most, exercised over a long random run
 * ------------------------------------------------------------------ */

test('no sequence of moves and resizes can produce an overlap', () => {
    // A deterministic pseudo-random walk: a fixed seed so a failure is
    // reproducible from the output alone, and enough operations that the
    // cascade paths actually get exercised rather than just the easy cases.
    let seed = 20260913;
    const rnd = (n) => {
        seed = (seed * 1103515245 + 12345) & 0x7fffffff;
        return seed % n;
    };

    let panels = [
        { id: 'rx-a', col: 0, row: 0, w: 6, h: 6 },
        { id: 'rx-b', col: 6, row: 0, w: 6, h: 6 },
        { id: 'spectrum', col: 0, row: 6, w: 12, h: 8 },
        { id: 'meters', col: 0, row: 14, w: 4, h: 4 },
        { id: 'memories', col: 4, row: 14, w: 8, h: 4 }
    ];

    for (let i = 0; i < 400; i++) {
        const id = panels[rnd(panels.length)].id;
        panels = rnd(2) === 0
            ? movePanel(panels, id, rnd(14) - 1, rnd(20) - 1, 12)
            : resizePanel(panels, id, rnd(14) - 1, rnd(10) - 1, 12);

        assert.equal(panels.length, 5, `iteration ${i}: a panel was lost`);
        assertNoOverlaps(panels, `iteration ${i}`);
        for (const p of panels) {
            assert.ok(p.col >= 0 && p.col + p.w <= 12, `iteration ${i}: ${p.id} is outside the grid`);
            assert.ok(p.row >= 0, `iteration ${i}: ${p.id} is above the top`);
            assert.ok(p.w >= MIN_W && p.h >= MIN_H, `iteration ${i}: ${p.id} collapsed`);
        }
    }
});

test('reflowing down through every breakpoint and back loses nothing', () => {
    // 12 -> 6 -> 1 -> 6 -> 12 is what happens when a layout built in the shack
    // is opened on a phone and then back on the monitor. Sizes are not
    // expected to survive the trip; the panels are.
    const start = [
        { id: 'rx-a', col: 0, row: 0, w: 6, h: 6 },
        { id: 'rx-b', col: 6, row: 0, w: 6, h: 6 },
        { id: 'spectrum', col: 0, row: 6, w: 12, h: 8 }
    ];
    let panels = start;
    for (const cols of [12, 6, 1, 6, 12]) {
        panels = reflow(panels, cols);
        assert.equal(panels.length, 3, `nothing may be lost at ${cols} columns`);
        assertNoOverlaps(panels, `${cols} columns`);
        for (const p of panels) {
            assert.ok(p.col + p.w <= cols, `${p.id} hangs off a ${cols}-column grid`);
        }
    }
    assert.deepEqual(panels.map(p => p.id).sort(), ['rx-a', 'rx-b', 'spectrum']);
});
