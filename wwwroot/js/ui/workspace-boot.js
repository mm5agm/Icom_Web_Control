/*
 * workspace-boot.js — IWC's half of the Workspace layout.
 *
 * The engine (core/js/layout/) knows about rectangles, storage and the DOM.
 * It does not know what an IC-7300 is. This file is the part that does: it
 * names the panels this application has, says how big each one wants to be,
 * says which two the operator must never be able to hide, and wires the
 * toolbar above the grid.
 *
 * The panels themselves are sections of Index.cshtml marked `data-panel`.
 * Nothing here builds or copies any markup — see core/js/layout/README.md for
 * why that matters.
 */

import { createWorkspace } from '/js/layout/workspace.js';

/**
 * What this station can show.
 *
 * `w`/`h` are in grid cells: 12 columns on a wide screen, and a cell is 40px
 * tall. The heights are a starting guess only: the engine measures each
 * panel the first time a default is laid out and gives it the rows its
 * content really needs, which depends on the radio (how many meters it has)
 * and cannot be known here. The spectrum is the exception: its canvas is
 * sized by script (spectrum-panel.js, from a ResizeObserver) after the
 * engine has measured, so measuring it reads a canvas that is not yet its
 * real height. It says `fills` and keeps its h — 10 rows holds the 280px
 * canvas plus its controls.
 *
 * `essential` is the escape route. The toolbar holds Connect and Radio Power
 * and VFO A holds the frequency and the band buttons; a layout persists, so
 * an operator who hid either would find them still hidden on the next load,
 * with the radio behind a puzzle. The engine renders no close button on these
 * and the rail's switch for them is disabled.
 *
 * The order is Classic's order, deliberately: the default Workspace layout is
 * the page the operator already knows, and the first thing they do is move
 * one panel, not rebuild the lot.
 */
function buildCatalogue() {
    return [
        {
            id: 'toolbar', title: 'Toolbar', group: 'Controls',
            w: 12, h: 3, minW: 4, minH: 2, essential: true, available: true
        },
        {
            id: 'meters', title: 'Meters', group: 'Displays',
            w: 12, h: 6, minW: 3, minH: 3, essential: false, available: true
        },
        {
            id: 'spectrum', title: 'Spectrum', group: 'Displays',
            w: 12, h: 10, minW: 3, minH: 4, essential: false, available: true, fills: true
        },
        {
            id: 'vfo-a', title: 'VFO A', group: 'Controls',
            w: 6, h: 22, minW: 3, minH: 6, essential: true, available: true
        },
        {
            id: 'vfo-b', title: 'VFO B', group: 'Controls',
            w: 6, h: 22, minW: 3, minH: 6, essential: false, available: true
        },
        {
            id: 'clarifier', title: 'Clarifier', group: 'Controls',
            w: 12, h: 3, minW: 3, minH: 2, essential: false, available: true
        }
    ];
}

/**
 * Presets name panels and say which want the full width. They never name a
 * position: the same preset then makes sense at 12, 6 and 1 columns, and on
 * the tablet in the shack as well as the monitor.
 */
const PRESETS = {
    'Ragchew':  { show: ['toolbar', 'vfo-a', 'meters', 'clarifier'], wide: ['toolbar', 'meters', 'clarifier'] },
    'Contest':  { show: ['toolbar', 'spectrum', 'vfo-a', 'vfo-b', 'meters'], wide: ['toolbar', 'spectrum', 'meters'] },
    'CW':       { show: ['toolbar', 'spectrum', 'vfo-a', 'meters'], wide: ['toolbar', 'spectrum', 'meters'] },
    'Barefoot': { show: ['toolbar', 'vfo-a'], wide: ['toolbar', 'vfo-a'] }
};

export function initWorkspace() {
    const container = document.getElementById('workspaceGrid');
    const rail = document.getElementById('workspaceRailList');
    if (!container) { return null; }

    const workspace = createWorkspace({
        container,
        rail,
        catalogue: buildCatalogue(),
        // Namespaced per application: IWC and YWC are often the same browser
        // on the same machine, and a Yaesu layout in an Icom page would be a
        // puzzle with no visible cause.
        prefix: 'iwc',
        presets: PRESETS,
        cellHeight: 40,
        gap: 8
    });

    const viewSelect = document.getElementById('uiViewSelect');
    const tools = document.getElementById('workspaceTools');
    const presetSelect = document.getElementById('workspacePresetSelect');
    const resetBtn = document.getElementById('workspaceResetBtn');
    const panelsBtn = document.getElementById('workspacePanelsBtn');

    let inWorkspace = false;

    function enter() {
        // The attribute is what core/css/layout-workspace.css is scoped to,
        // so setting it is what makes the sheet apply at all. _Layout has
        // already rendered it for the first paint; this keeps the two in step
        // when the operator switches view.
        document.documentElement.setAttribute('data-layout', 'workspace');
        workspace.mount();
        inWorkspace = true;
        if (viewSelect) { viewSelect.value = 'Workspace'; }
        if (tools) { tools.hidden = false; }
    }

    function leave() {
        // unmount() puts every adopted element back where it came from, so
        // this is a genuine return to Classic rather than a Classic-looking
        // arrangement. Nothing is thrown away: the layout stays saved, and
        // coming back re-mounts it.
        workspace.unmount();
        document.documentElement.removeAttribute('data-layout');
        inWorkspace = false;
        if (viewSelect) { viewSelect.value = 'Classic'; }
        // The rail and the presets act on a grid that is no longer there, and
        // a Classic page should show nothing it did not show before.
        if (tools) { tools.hidden = true; }
        const railPanel = document.getElementById('workspaceRail');
        if (railPanel) { railPanel.classList.remove('show'); }
        if (panelsBtn) { panelsBtn.setAttribute('aria-expanded', 'false'); }
    }

    // The View selector is the one place the choice is made, and this
    // browser remembers it. Settings > Appearance is only the default for a
    // browser that has never chosen, which is what the <select> was rendered
    // with; a remembered choice beats it, and the head script in _Layout
    // has already put the html attribute in step with it before first paint.
    const VIEW_KEY = 'iwc.view';
    function rememberedView() {
        try {
            const v = localStorage.getItem(VIEW_KEY);
            return (v === 'Workspace' || v === 'Classic') ? v : null;
        } catch {
            return null;
        }
    }
    function rememberView(v) {
        try { localStorage.setItem(VIEW_KEY, v); } catch { /* private browsing: the choice lasts this page load */ }
    }

    viewSelect?.addEventListener('change', () => {
        const v = viewSelect.value === 'Workspace' ? 'Workspace' : 'Classic';
        rememberView(v);
        if (v === 'Workspace') { if (!inWorkspace) { enter(); } }
        else if (inWorkspace) { leave(); }
    });

    if (presetSelect) {
        for (const name of workspace.presetNames()) {
            const option = document.createElement('option');
            option.value = name;
            option.textContent = name;
            presetSelect.appendChild(option);
        }
        presetSelect.addEventListener('change', () => {
            const name = presetSelect.value;
            if (!name) { return; }
            workspace.usePreset(name);
            // Back to the placeholder: a preset is a starting point, and
            // leaving it selected suggests the arrangement on screen is still
            // that preset after the operator has moved three panels.
            presetSelect.value = '';
        });
    }

    resetBtn?.addEventListener('click', () => { workspace.reset(); });

    const startIn = rememberedView() || (viewSelect ? viewSelect.value : 'Classic');
    if (startIn === 'Workspace') { enter(); } else { leave(); }

    // Handy from the console while this is new, and the only global it adds.
    window.iwcWorkspace = workspace;
    return workspace;
}

if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initWorkspace, { once: true });
} else {
    initWorkspace();
}
