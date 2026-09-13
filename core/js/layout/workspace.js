/*
 * workspace.js — the DOM half of the dockable-panel workspace.
 * Shared by Icom Web Control and Yaesu Web Control.
 *
 * layout-grid.js decides where panels go; this puts them there.
 *
 * THE ONE IDEA THIS FILE IS BUILT ON
 * ----------------------------------
 * The workspace does NOT build a second copy of the user interface. It MOVES
 * the elements that are already on the page into panel shells, and moves them
 * back when the operator switches to Classic.
 *
 * That is not a shortcut, it is the only approach that can work here. Both
 * applications find every control by id — some five hundred
 * `getElementById` calls between them — and not one of them cares which
 * container the element sits in. So relocating a <div> with
 * `appendChild` leaves every live value, every event handler, every SignalR
 * update and all 396 aria attributes working, because none of them were ever
 * attached to the position.
 *
 * Cloning, re-rendering, or building the panels from a template would break
 * all of it at once, silently, and in a way that only shows up against a
 * radio. `appendChild` on a live node is the boring answer and it is right.
 *
 * The rule that follows from it: this file may move elements and it may set
 * geometry on the shells it created. It may not alter the markup inside a
 * panel, and it may not touch an id.
 *
 * WHAT "ESSENTIAL" BUYS
 * --------------------
 * A layout is persisted, so a layout that hides everything hides everything
 * on the next load too, and the operator's radio is now behind a puzzle. The
 * escape hatch is enforced in layout-model.js; this file simply refuses to
 * render a close button on a panel the catalogue marked essential.
 */

import {
    normaliseCatalogue, defaultLayout, reconcile, applyPreset, LAYOUT_VERSION
} from './layout-model.js';
import {
    movePanel, resizePanel, reflow, findSlot, gridRows, normaliseRect
} from './layout-grid.js';
import { createLayoutStore, DEFAULT_LAYOUT_NAME } from './layout-store.js';

/**
 * Columns for a given viewport width.
 *
 * Three steps, not a continuum: a layout that reflows on every pixel of
 * resize is one the operator cannot save, because the thing they saved is
 * not the thing they get back. The 12/6/1 breakpoints line up with the
 * Bootstrap grid both applications already use.
 */
export function columnsForWidth(width) {
    if (width >= 1400) { return 12; }
    if (width >= 900) { return 6; }
    return 1;
}

/**
 * Mount a workspace.
 *
 * @param {object} opts
 * @param {HTMLElement} opts.container  where panels are placed
 * @param {HTMLElement} [opts.rail]     where the panel list is drawn
 * @param {Array} opts.catalogue        the application's panel catalogue
 * @param {string} opts.prefix          storage prefix, "iwc" / "ywc"
 * @param {object} [opts.presets]       named presets, { name: {show, wide} }
 * @param {number} [opts.cellHeight]    row height in px (default 40)
 * @param {number} [opts.gap]           gutter in px (default 8)
 */
export function createWorkspace(opts) {
    const container = opts.container;
    const rail = opts.rail || null;
    const catalogue = normaliseCatalogue(opts.catalogue);
    const store = createLayoutStore(opts.prefix, opts.storage);
    const presets = opts.presets || {};
    const cellHeight = Math.max(8, Math.floor(opts.cellHeight) || 40);
    const gap = Math.max(0, Math.floor(opts.gap ?? 8));

    // Where each adopted element came from, so Classic can be restored
    // exactly. A comment node is used as the marker rather than an index,
    // because anything else on the page may have inserted or removed siblings
    // in between — a dialog, a hidden row, a panel the operator closed.
    const origins = new Map();
    const shells = new Map();

    let columns = columnsForWidth(window.innerWidth);
    let layout = null;
    let mounted = false;
    let dragState = null;

    const byId = new Map(catalogue.map(p => [p.id, p]));

    function panelSpec(id) { return byId.get(id); }

    /* ---------------------------------------------------------------- *
     * Adopting and returning elements
     * ---------------------------------------------------------------- */

    function sourceElement(id) {
        // The application marks its own sections; the engine never guesses.
        return document.querySelector(`[data-panel="${CSS.escape(id)}"]`);
    }

    function adopt(id, body) {
        const el = sourceElement(id);
        if (!el) { return false; }
        if (!origins.has(id)) {
            const marker = document.createComment(` panel:${id} `);
            el.parentNode.insertBefore(marker, el);
            origins.set(id, marker);
        }
        body.appendChild(el);
        return true;
    }

    function returnHome(id) {
        const marker = origins.get(id);
        const el = sourceElement(id);
        if (marker && el && marker.parentNode) {
            marker.parentNode.insertBefore(el, marker);
        }
    }

    /* ---------------------------------------------------------------- *
     * Shells
     * ---------------------------------------------------------------- */

    function buildShell(id) {
        const spec = panelSpec(id);
        const shell = document.createElement('section');
        shell.className = 'rwc-panel';
        shell.dataset.panelId = id;
        // The shell is a labelled region, so a screen reader announces moving
        // into it by the same name the operator sees in the rail. The panels
        // being reachable in DOM order is what keeps Tab sensible: the visual
        // grid can put a panel anywhere, but the tab sequence follows the
        // layout's reading order because that is the order they are appended.
        shell.setAttribute('role', 'region');
        shell.setAttribute('aria-label', spec ? spec.title : id);

        const header = document.createElement('header');
        header.className = 'rwc-panel-header';

        const handle = document.createElement('button');
        handle.type = 'button';
        handle.className = 'rwc-panel-handle';
        handle.setAttribute('aria-label',
            `Move ${spec ? spec.title : id}. Use the arrow keys to move it, hold Shift to resize.`);
        handle.innerHTML = '<span aria-hidden="true">&#8942;&#8942;</span>';

        const title = document.createElement('h2');
        title.className = 'rwc-panel-title';
        title.textContent = spec ? spec.title : id;

        header.appendChild(handle);
        header.appendChild(title);

        // No close button on an essential panel: see the header comment.
        if (!spec || !spec.essential) {
            const close = document.createElement('button');
            close.type = 'button';
            close.className = 'rwc-panel-close';
            close.setAttribute('aria-label', `Hide ${spec ? spec.title : id}`);
            close.innerHTML = '<span aria-hidden="true">&times;</span>';
            close.addEventListener('click', () => hide(id));
            header.appendChild(close);
        }

        const body = document.createElement('div');
        body.className = 'rwc-panel-body';

        const grip = document.createElement('div');
        grip.className = 'rwc-panel-resize';
        // Decorative: resizing is done from the handle with Shift+arrows,
        // which is reachable without a pointer. A second focusable control
        // doing the same job would only lengthen the tab sequence.
        grip.setAttribute('aria-hidden', 'true');

        shell.appendChild(header);
        shell.appendChild(body);
        shell.appendChild(grip);

        wireDrag(shell, handle, grip, id);
        return { shell, body, handle };
    }

    function place(shell, rect) {
        // CSS Grid does the arithmetic; this only says which lines to span.
        // Doing it with grid-area rather than absolute positioning is what
        // lets a panel's own content decide it needs to be taller than its
        // rows without being clipped.
        shell.style.gridColumn = `${rect.col + 1} / span ${rect.w}`;
        shell.style.gridRow = `${rect.row + 1} / span ${rect.h}`;
    }

    function render() {
        container.style.display = 'grid';
        container.style.gridTemplateColumns = `repeat(${columns}, minmax(0, 1fr))`;
        container.style.gridAutoRows = `${cellHeight}px`;
        container.style.gap = `${gap}px`;
        container.style.alignItems = 'stretch';

        const wanted = new Set(layout.panels.map(p => p.id));

        // Remove shells for panels that are no longer in the layout, giving
        // their contents back to the page first.
        for (const [id, entry] of shells) {
            if (!wanted.has(id)) {
                returnHome(id);
                entry.shell.remove();
                shells.delete(id);
            }
        }

        // Append in the layout's reading order so the tab sequence matches
        // what the operator sees, top-left to bottom-right.
        for (const p of layout.panels) {
            let entry = shells.get(p.id);
            if (!entry) {
                entry = buildShell(p.id);
                shells.set(p.id, entry);
                if (!adopt(p.id, entry.body)) {
                    // The application listed a panel whose markup is not on
                    // this page. Drop the shell rather than render an empty
                    // box the operator will report as a broken panel.
                    entry.shell.remove();
                    shells.delete(p.id);
                    continue;
                }
            }
            place(entry.shell, p);
            container.appendChild(entry.shell);
        }

        container.style.minHeight = `${gridRows(layout.panels) * (cellHeight + gap)}px`;
        renderRail();
    }

    /* ---------------------------------------------------------------- *
     * The rail — what this station can show
     * ---------------------------------------------------------------- */

    function renderRail() {
        if (!rail) { return; }
        rail.textContent = '';
        const shown = new Set(layout.panels.map(p => p.id));
        const groups = new Map();
        for (const spec of catalogue) {
            if (!spec.available) { continue; }
            if (!groups.has(spec.group)) { groups.set(spec.group, []); }
            groups.get(spec.group).push(spec);
        }

        for (const [name, items] of groups) {
            const section = document.createElement('div');
            section.className = 'rwc-rail-group';
            const heading = document.createElement('h3');
            heading.className = 'rwc-rail-heading';
            heading.textContent = name;
            section.appendChild(heading);

            for (const spec of items) {
                const row = document.createElement('div');
                row.className = 'rwc-rail-item form-check form-switch';
                const input = document.createElement('input');
                input.className = 'form-check-input';
                input.type = 'checkbox';
                input.role = 'switch';
                input.id = `rwc-rail-${spec.id}`;
                input.checked = shown.has(spec.id);
                input.disabled = spec.essential;
                input.addEventListener('change', () => {
                    if (input.checked) { show(spec.id); } else { hide(spec.id); }
                });
                const label = document.createElement('label');
                label.className = 'form-check-label';
                label.setAttribute('for', input.id);
                label.textContent = spec.title;
                if (spec.essential) {
                    // Say why it cannot be switched off, rather than leaving a
                    // greyed-out switch with no explanation.
                    label.title = 'Always shown';
                }
                row.appendChild(input);
                row.appendChild(label);
                section.appendChild(row);
            }
            rail.appendChild(section);
        }
    }

    /* ---------------------------------------------------------------- *
     * Moving and resizing
     * ---------------------------------------------------------------- */

    function cellSize() {
        const rect = container.getBoundingClientRect();
        const colWidth = (rect.width - gap * (columns - 1)) / columns;
        return { colWidth: Math.max(1, colWidth), rowHeight: cellHeight + gap, rect };
    }

    function wireDrag(shell, handle, grip, id) {
        // Pointer events rather than mouse events: one code path covers mouse,
        // touch and pen, and setPointerCapture keeps the drag alive when the
        // pointer leaves the shell — which it does constantly, because the
        // shell is what is moving.
        const start = (mode) => (ev) => {
            if (ev.button !== undefined && ev.button !== 0) { return; }
            const p = layout.panels.find(x => x.id === id);
            if (!p) { return; }
            ev.preventDefault();
            handle.setPointerCapture?.(ev.pointerId);
            dragState = {
                id, mode, pointerId: ev.pointerId,
                startX: ev.clientX, startY: ev.clientY,
                origin: { ...p }, target: ev.currentTarget
            };
            ev.currentTarget.setPointerCapture?.(ev.pointerId);
            shell.classList.add('rwc-panel-dragging');
        };
        handle.addEventListener('pointerdown', start('move'));
        grip.addEventListener('pointerdown', start('resize'));

        handle.addEventListener('keydown', (ev) => onHandleKey(ev, id));
    }

    function onPointerMove(ev) {
        if (!dragState || ev.pointerId !== dragState.pointerId) { return; }
        const { colWidth, rowHeight } = cellSize();
        const dCol = Math.round((ev.clientX - dragState.startX) / colWidth);
        const dRow = Math.round((ev.clientY - dragState.startY) / rowHeight);
        const o = dragState.origin;
        if (dragState.mode === 'move') {
            apply(movePanel(layout.panels, dragState.id, o.col + dCol, o.row + dRow, columns));
        } else {
            apply(resizePanel(layout.panels, dragState.id, o.w + dCol, o.h + dRow, columns));
        }
    }

    function onPointerUp(ev) {
        if (!dragState || ev.pointerId !== dragState.pointerId) { return; }
        const entry = shells.get(dragState.id);
        entry?.shell.classList.remove('rwc-panel-dragging');
        dragState.target?.releasePointerCapture?.(ev.pointerId);
        dragState = null;
        persist();
    }

    /**
     * Keyboard move and resize.
     *
     * Not an afterthought and not optional: both applications have operators
     * who cannot use a pointing device at all — head-tracking and on-screen
     * keyboards — and a workspace only reachable by dragging would take the
     * layout away from exactly the people a rearrangeable layout helps most.
     * Arrows move, Shift+arrows resize, and the panel keeps focus throughout
     * so the change is announced against the region's own name.
     */
    function onHandleKey(ev, id) {
        const p = layout.panels.find(x => x.id === id);
        if (!p) { return; }
        let dc = 0, dr = 0;
        switch (ev.key) {
            case 'ArrowLeft': dc = -1; break;
            case 'ArrowRight': dc = 1; break;
            case 'ArrowUp': dr = -1; break;
            case 'ArrowDown': dr = 1; break;
            default: return;
        }
        ev.preventDefault();
        if (ev.shiftKey) {
            apply(resizePanel(layout.panels, id, p.w + dc, p.h + dr, columns));
        } else {
            apply(movePanel(layout.panels, id, p.col + dc, p.row + dr, columns));
        }
        persist();
        // Re-focus: render() re-appends the shells, which drops focus.
        shells.get(id)?.handle.focus();
    }

    /* ---------------------------------------------------------------- *
     * State
     * ---------------------------------------------------------------- */

    function apply(panels) {
        layout = { ...layout, panels };
        render();
    }

    function persist() {
        const hidden = catalogue
            .filter(s => s.available && !layout.panels.some(p => p.id === s.id))
            .map(s => s.id);
        layout.hidden = hidden;
        store.save(store.currentName(), { ...layout, columns });
    }

    function show(id) {
        if (layout.panels.some(p => p.id === id)) { return; }
        const spec = panelSpec(id);
        if (!spec) { return; }
        const slot = findSlot(layout.panels, Math.min(columns, spec.w), spec.h, columns);
        apply([...layout.panels, { id, ...slot }]);
        persist();
    }

    function hide(id) {
        const spec = panelSpec(id);
        if (spec && spec.essential) { return; }
        apply(layout.panels.filter(p => p.id !== id));
        persist();
    }

    function load(name) {
        const stored = store.load(name);
        const base = stored
            ? reconcile(stored, catalogue, columns)
            : defaultLayout(catalogue, columns);
        layout = { ...base, panels: reflow(base.panels, columns), columns };
    }

    function onResize() {
        const next = columnsForWidth(window.innerWidth);
        if (next === columns) { return; }
        columns = next;
        // Reflowed, not saved. A layout the operator built on the wide screen
        // must still be there when they come back to it, so a narrow visit
        // is allowed to rearrange the view but never the stored arrangement.
        layout = { ...layout, columns, panels: reflow(layout.panels, columns) };
        render();
    }

    return {
        get columns() { return columns; },
        get layout() { return layout ? { ...layout, version: LAYOUT_VERSION } : null; },

        mount(name) {
            if (mounted) { return; }
            columns = columnsForWidth(window.innerWidth);
            store.setCurrentName(name || store.currentName());
            load(store.currentName());
            container.hidden = false;
            render();
            window.addEventListener('resize', onResize);
            window.addEventListener('pointermove', onPointerMove);
            window.addEventListener('pointerup', onPointerUp);
            window.addEventListener('pointercancel', onPointerUp);
            mounted = true;
        },

        /**
         * Put every element back where it came from and take the grid down.
         * This is what makes the Classic/Workspace switch reversible without
         * a page reload, and it is the reason `origins` holds comment markers
         * rather than indices.
         */
        unmount() {
            if (!mounted) { return; }
            for (const [id, entry] of shells) {
                returnHome(id);
                entry.shell.remove();
            }
            shells.clear();
            container.hidden = true;
            container.removeAttribute('style');
            window.removeEventListener('resize', onResize);
            window.removeEventListener('pointermove', onPointerMove);
            window.removeEventListener('pointerup', onPointerUp);
            window.removeEventListener('pointercancel', onPointerUp);
            mounted = false;
        },

        show,
        hide,

        /** Switch to a named saved layout. */
        switchTo(name) {
            store.setCurrentName(name);
            load(name);
            render();
        },

        /** Save the current arrangement under a name. */
        saveAs(name) {
            store.save(name, { ...layout, columns });
            store.setCurrentName(name);
        },

        names: () => store.names(),
        currentName: () => store.currentName(),

        /** Apply a named preset (Ragchew / Contest / CW / Digital). */
        usePreset(name) {
            const preset = presets[name];
            if (!preset) { return false; }
            const next = applyPreset(preset, catalogue, columns);
            layout = { ...next, panels: reflow(next.panels, columns) };
            render();
            persist();
            return true;
        },

        /** Throw the arrangement away and start from the default again. */
        reset() {
            const base = defaultLayout(catalogue, columns);
            layout = { ...base, panels: reflow(base.panels, columns) };
            render();
            persist();
        },

        presetNames: () => Object.keys(presets)
    };
}

export { DEFAULT_LAYOUT_NAME, normaliseRect };
