/*
 * layout-grid.js — the geometry of a dockable-panel workspace.
 * Shared by Icom Web Control and Yaesu Web Control.
 *
 * Pure functions over plain objects. No DOM, no storage, no radio, nothing
 * that knows what a panel CONTAINS — a rect here is as likely to be a
 * spectrum display as a clock. That is deliberate and it is the whole reason
 * this file can be shared: the two applications' panels have nothing in
 * common, but a grid is a grid.
 *
 * It is also the only part of the layout work that can be unit-tested, so it
 * is where the fiddly reasoning is put on purpose. Dragging a panel with a
 * mouse is not testable in `node --test`; deciding where the panel lands is.
 *
 * THE COORDINATE SYSTEM
 * ---------------------
 * Columns are integers, 0 .. columns-1. Rows are integers from 0 with no
 * upper bound — the workspace scrolls downwards, so there is no such thing as
 * running out of vertical room, and every "does it fit" question is therefore
 * only ever about width.
 *
 * A rect is { col, row, w, h }, w and h in whole cells, w >= 1 and h >= 1.
 * A placed panel is { id, ...rect }.
 *
 * WHY IT PUSHES DOWNWARD AND NEVER SIDEWAYS
 * -----------------------------------------
 * When a moved panel overlaps others, the ones it hits move DOWN, never left
 * or right. An operator's muscle memory is built on horizontal position — the
 * band buttons are on the left of the receiver panel and always have been —
 * and a sideways shuffle breaks that for panels the operator never touched.
 * Downward displacement preserves left-to-right order absolutely, which is
 * what makes the result predictable enough to undo by eye.
 */

/** The smallest a panel may be, in cells. */
export const MIN_W = 1;
export const MIN_H = 1;

/** Clamp n to [lo, hi], returning lo when the range is empty. */
function clamp(n, lo, hi) {
    if (!(hi >= lo)) { return lo; }
    return n < lo ? lo : (n > hi ? hi : n);
}

/**
 * Coerce anything into a valid rect inside a grid `columns` wide.
 *
 * Called on every rect that comes from outside this module — stored layouts
 * from an older version, a hand-edited localStorage value, a panel catalogue
 * with a typo. A layout that cannot be trusted must still render something,
 * because the alternative is an operator staring at an empty page with no way
 * to get their radio back.
 */
export function normaliseRect(rect, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const r = rect || {};
    // Width first: it bounds the column, not the other way round. Doing this
    // in the other order lets a wide panel at a high column silently shrink.
    const w = clamp(Math.floor(r.w) || MIN_W, MIN_W, cols);
    const h = Math.max(MIN_H, Math.floor(r.h) || MIN_H);
    const col = clamp(Math.floor(r.col) || 0, 0, cols - w);
    const row = Math.max(0, Math.floor(r.row) || 0);
    return { col, row, w, h };
}

/** True when two rects share at least one cell. */
export function overlaps(a, b) {
    return a.col < b.col + b.w
        && b.col < a.col + a.w
        && a.row < b.row + b.h
        && b.row < a.row + a.h;
}

/**
 * Sort order for laying out: top to bottom, then left to right.
 * Every pass below relies on this, so it lives here rather than being
 * open-coded at three call sites with one of them subtly different.
 */
export function readingOrder(a, b) {
    return (a.row - b.row) || (a.col - b.col) || String(a.id).localeCompare(String(b.id));
}

/**
 * Resolve overlaps by pushing collided panels DOWN, cascading.
 *
 * `anchorId` is the panel that must not move — the one the operator is
 * dragging. Everything else yields to it. With no anchor, earlier panels in
 * reading order win, which is what a freshly-loaded layout wants.
 *
 * Returns a new array; the input is not modified.
 */
export function resolveCollisions(panels, anchorId) {
    const out = panels.map(p => ({ ...p }));
    // The anchor is settled first so that everything else is tested against
    // its final position rather than chasing it.
    const order = out.slice().sort((a, b) => {
        if (a.id === anchorId) { return -1; }
        if (b.id === anchorId) { return 1; }
        return readingOrder(a, b);
    });

    const settled = [];
    for (const panel of order) {
        // Walk down until it sits clear. Each step is one row, because a
        // bigger jump would leave gaps that compact() then has to undo, and
        // the two passes disagreeing is how panels end up jittering.
        let guard = 0;
        for (;;) {
            const hit = settled.find(s => overlaps(s, panel));
            if (!hit) { break; }
            panel.row = hit.row + hit.h;
            // A grid this size cannot need more iterations than it has
            // panels; the counter is here so a future bug cannot hang the
            // browser, not because the loop is expected to run away.
            if (++guard > out.length + 1) { break; }
        }
        settled.push(panel);
    }
    return out;
}

/**
 * Pull every panel up into whatever space is free above it.
 *
 * Run after any move, resize or removal. Without it, dragging a panel out of
 * the top row leaves a permanent band of empty grid that the operator cannot
 * get rid of except by dragging everything up by hand.
 */
export function compact(panels) {
    const out = panels.map(p => ({ ...p })).sort(readingOrder);
    const settled = [];
    for (const panel of out) {
        while (panel.row > 0) {
            const probe = { ...panel, row: panel.row - 1 };
            if (settled.some(s => overlaps(s, probe))) { break; }
            panel.row = probe.row;
        }
        settled.push(panel);
    }
    return out;
}

/**
 * Move one panel to (col, row) and settle the rest.
 * Returns a new array, in reading order.
 */
export function movePanel(panels, id, col, row, columns) {
    const next = panels.map(p => {
        if (p.id !== id) { return { ...p }; }
        const r = normaliseRect({ ...p, col, row }, columns);
        return { ...p, ...r };
    });
    return compact(resolveCollisions(next, id)).sort(readingOrder);
}

/**
 * Resize one panel and settle the rest.
 *
 * A resize is anchored at the panel's top-left, so growing it never moves the
 * corner the operator is looking at. Growing past the right edge shrinks the
 * width rather than sliding the panel left — sliding would move a panel the
 * operator is actively dragging the opposite edge of, which reads as the
 * application fighting them.
 */
export function resizePanel(panels, id, w, h, columns) {
    const next = panels.map(p => {
        if (p.id !== id) { return { ...p }; }
        const cols = Math.max(1, Math.floor(columns) || 1);
        const width = clamp(Math.floor(w) || MIN_W, MIN_W, cols - p.col);
        const height = Math.max(MIN_H, Math.floor(h) || MIN_H);
        return { ...p, w: width, h: height };
    });
    return compact(resolveCollisions(next, id)).sort(readingOrder);
}

/**
 * Fit a layout to a narrower grid.
 *
 * Called when the window shrinks past a breakpoint, and when a layout saved
 * on the shack's wide monitor is opened on the bench tablet. Panels too wide
 * for the new grid are narrowed rather than dropped: a panel the operator
 * cannot see is indistinguishable from one the application has lost.
 */
export function reflow(panels, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const next = panels.map(p => ({ ...p, ...normaliseRect(p, cols) }));
    return compact(resolveCollisions(next)).sort(readingOrder);
}

/**
 * Find the first place a w x h panel fits, scanning in reading order.
 * Used when a panel is switched on from the rail: it should appear
 * somewhere sensible without displacing anything already placed.
 */
export function findSlot(panels, w, h, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const width = clamp(Math.floor(w) || MIN_W, MIN_W, cols);
    const height = Math.max(MIN_H, Math.floor(h) || MIN_H);
    // One row past the bottom of everything is always free, so this
    // terminates whatever the contents.
    const maxRow = panels.reduce((m, p) => Math.max(m, p.row + p.h), 0);
    for (let row = 0; row <= maxRow; row++) {
        for (let col = 0; col <= cols - width; col++) {
            const probe = { col, row, w: width, h: height };
            if (!panels.some(p => overlaps(p, probe))) { return probe; }
        }
    }
    return { col: 0, row: maxRow, w: width, h: height };
}

/** Total grid height in rows — what the container must be tall enough for. */
export function gridRows(panels) {
    return panels.reduce((m, p) => Math.max(m, p.row + p.h), 0);
}
