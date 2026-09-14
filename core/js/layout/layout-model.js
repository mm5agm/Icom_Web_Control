/*
 * layout-model.js — what a workspace layout IS, and what makes one valid.
 * Shared by Icom Web Control and Yaesu Web Control.
 *
 * Pure functions over plain objects: no DOM, no storage, no radio.
 *
 * THE SEAM
 * --------
 * A *catalogue* is the list of panels an application offers, and it is built
 * by the application from its own capabilities — IWC asks whether the radio
 * has a second receiver and whether an SDR is configured, YWC asks about
 * those plus VC Tune and video capture. None of that can be shared and none
 * of it is here.
 *
 * A *layout* is where those panels sit, which is the same problem in both
 * applications, and is all this file knows about.
 *
 * The catalogue entry an application supplies:
 *
 *   {
 *     id:        'receiver-a',      // stable; it is what gets persisted
 *     title:     'Receiver A',      // shown in the rail and the panel header
 *     group:     'Receivers',       // how the rail groups it
 *     w: 6, h: 8,                   // default size in grid cells
 *     minW: 4, minH: 4,             // optional floors
 *     essential: true,              // cannot be switched off (see below)
 *     available: true,              // this station can show it at all
 *     fills: true                   // content stretches to any height (a
 *                                   // canvas), so never size it to content
 *   }
 *
 * WHY `essential` EXISTS
 * ----------------------
 * A workspace that can hide everything can hide the thing the operator needs
 * to get their radio back, and a stored layout survives a reload — so the
 * escape route has to be one the operator cannot switch off by accident. A
 * panel marked essential is always present in a valid layout, whatever the
 * stored one says. Each application decides what qualifies; the engine only
 * enforces it.
 */

/** The layout format's version. Bumped only when a migration is needed. */
export const LAYOUT_VERSION = 1;

/**
 * Normalise a catalogue entry, filling in what the application left out.
 * Returns null for an entry with no usable id, because a panel that cannot
 * be addressed cannot be persisted either.
 */
export function normalisePanel(entry) {
    if (!entry || typeof entry.id !== 'string' || entry.id.trim() === '') {
        return null;
    }
    const id = entry.id.trim();
    const minW = Math.max(1, Math.floor(entry.minW) || 1);
    const minH = Math.max(1, Math.floor(entry.minH) || 1);
    return {
        id,
        title: typeof entry.title === 'string' && entry.title ? entry.title : id,
        group: typeof entry.group === 'string' && entry.group ? entry.group : 'Panels',
        w: Math.max(minW, Math.floor(entry.w) || minW),
        h: Math.max(minH, Math.floor(entry.h) || minH),
        minW,
        minH,
        essential: entry.essential === true,
        // Absent means available: a catalogue that forgets the flag should
        // show the panel, not silently withhold it.
        available: entry.available !== false,
        fills: entry.fills === true
    };
}

/** Normalise a whole catalogue, dropping unusable entries and duplicate ids. */
export function normaliseCatalogue(entries) {
    const seen = new Set();
    const out = [];
    for (const e of entries || []) {
        const p = normalisePanel(e);
        if (!p || seen.has(p.id)) { continue; }
        seen.add(p.id);
        out.push(p);
    }
    return out;
}

/**
 * The layout an application falls back to: every available panel, in
 * catalogue order, at its catalogue size, packed left to right along a row
 * and wrapping to a fresh row when the next one does not fit.
 *
 * Catalogue order and catalogue widths on purpose — the application chose
 * those to mirror the page the operator already has (two receivers side by
 * side, the meters full width), so the first thing they see when they switch
 * to Workspace is recognisable rather than a puzzle. Narrow the grid and the
 * same rule stacks everything, which is what the classic page does too.
 */
export function defaultLayout(catalogue, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    let row = 0, col = 0, rowH = 0;
    const panels = [];
    for (const p of catalogue) {
        if (!p.available) { continue; }
        const w = Math.min(cols, Math.max(p.minW, p.w));
        const h = Math.max(p.minH, p.h);
        if (col + w > cols) { row += rowH; col = 0; rowH = 0; }
        panels.push({ id: p.id, col, row, w, h });
        col += w;
        rowH = Math.max(rowH, h);
    }
    return { version: LAYOUT_VERSION, columns: cols, panels };
}

/**
 * Reconcile a stored layout against the catalogue that is actually available
 * now. This is the function that has to cope with reality:
 *
 *   - a panel in the layout that no longer exists (the operator unplugged the
 *     SDR, or upgraded from a version that had it) — dropped
 *   - a panel in the catalogue that the layout has never seen (a new feature
 *     shipped since the layout was saved) — appended, not silently withheld,
 *     because a feature the operator cannot find is a feature they will
 *     report as missing
 *   - an essential panel the stored layout has dropped — restored
 *
 * Geometry is left alone here; the caller runs it through layout-grid's
 * reflow(), which is where "does it fit" lives.
 */
export function reconcile(stored, catalogue, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const available = catalogue.filter(p => p.available);
    const byId = new Map(available.map(p => [p.id, p]));

    const storedPanels = Array.isArray(stored && stored.panels) ? stored.panels : [];
    const kept = [];
    const seen = new Set();
    for (const sp of storedPanels) {
        const spec = sp && byId.get(sp.id);
        if (!spec || seen.has(sp.id)) { continue; }
        seen.add(sp.id);
        kept.push({
            id: spec.id,
            col: Math.max(0, Math.floor(sp.col) || 0),
            row: Math.max(0, Math.floor(sp.row) || 0),
            w: Math.max(spec.minW, Math.floor(sp.w) || spec.w),
            h: Math.max(spec.minH, Math.floor(sp.h) || spec.h)
        });
    }

    // Anything the layout has not seen goes at the bottom, full width. New
    // panels are rare and arriving in a stack is easier to notice — and to
    // move — than one tucked into a gap somewhere in the middle.
    let nextRow = kept.reduce((m, p) => Math.max(m, p.row + p.h), 0);
    for (const spec of available) {
        if (seen.has(spec.id)) { continue; }
        // Only essential panels are auto-added when the operator has
        // deliberately hidden things; a brand-new optional panel still
        // appears, but the hidden ones they chose stay hidden — the
        // difference is whether the id was ever in the stored layout, which
        // `hidden` below records.
        const wasHidden = Array.isArray(stored && stored.hidden)
            && stored.hidden.includes(spec.id);
        if (wasHidden && !spec.essential) { continue; }
        kept.push({ id: spec.id, col: 0, row: nextRow, w: cols, h: spec.h });
        nextRow += spec.h;
        seen.add(spec.id);
    }

    // Whatever the operator has switched off, minus anything that has since
    // stopped being available — so unplugging and replugging an SDR does not
    // resurrect a panel they hid.
    const hidden = available
        .filter(p => !seen.has(p.id) && !p.essential)
        .map(p => p.id);

    return { version: LAYOUT_VERSION, columns: cols, panels: kept, hidden };
}

/**
 * A named starting point: Ragchew, Contest, CW, Digital.
 *
 * A preset names panels and relative sizes, never absolute positions, because
 * the same preset has to make sense on a 12-column monitor and a 4-column
 * tablet. `show` is the panel ids in the order they should stack; `wide` are
 * the ones that get the full width even when others are paired up.
 */
export function applyPreset(preset, catalogue, columns) {
    const cols = Math.max(1, Math.floor(columns) || 1);
    const byId = new Map(catalogue.filter(p => p.available).map(p => [p.id, p]));
    const wide = new Set(preset && preset.wide ? preset.wide : []);
    const ids = (preset && preset.show ? preset.show : []).filter(id => byId.has(id));

    // Essential panels are added even when the preset forgot them — a preset
    // is a convenience, not a way to lock the operator out of their radio.
    for (const p of byId.values()) {
        if (p.essential && !ids.includes(p.id)) { ids.unshift(p.id); }
    }

    const panels = [];
    let row = 0;
    let col = 0;
    let rowHeight = 0;
    const half = Math.max(1, Math.floor(cols / 2));

    for (const id of ids) {
        const spec = byId.get(id);
        // Pairing only makes sense when half a grid is still wide enough for
        // the panel's own minimum. Below that, everything is full width —
        // which is what a phone-width viewport gets, and correctly so.
        const wantsFull = wide.has(id) || half < spec.minW;
        const w = wantsFull ? cols : half;

        if (wantsFull && col !== 0) { row += rowHeight; col = 0; rowHeight = 0; }
        panels.push({ id, col, row, w, h: Math.max(spec.minH, spec.h) });
        rowHeight = Math.max(rowHeight, Math.max(spec.minH, spec.h));

        if (wantsFull || col + w >= cols) { row += rowHeight; col = 0; rowHeight = 0; }
        else { col += w; }
    }

    const shown = new Set(panels.map(p => p.id));
    const hidden = [...byId.values()].filter(p => !shown.has(p.id)).map(p => p.id);
    return { version: LAYOUT_VERSION, columns: cols, panels, hidden };
}

/** True when the two layouts place the same panels in the same cells. */
export function layoutsEqual(a, b) {
    const pa = (a && a.panels) || [];
    const pb = (b && b.panels) || [];
    if (pa.length !== pb.length) { return false; }
    const key = p => `${p.id}:${p.col},${p.row},${p.w},${p.h}`;
    const sa = pa.map(key).sort();
    const sb = pb.map(key).sort();
    return sa.every((v, i) => v === sb[i]);
}
