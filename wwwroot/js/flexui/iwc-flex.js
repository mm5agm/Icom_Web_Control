/**
 * Experimental UI (/flexui) - IWC's configuration of the shared dock.
 *
 * /flexui serves the same Index page as Classic, in _FlexLayout. Everything
 * on it has been bound by site.js and Index's own scripts by the time this
 * module runs (it is the last script on the page), so the panels are not
 * re-created from templates as YWC's are: this module moves the page's own
 * sections into `#ywcFlexParking > .ywc-panel-body[data-ywc-component]`
 * wrappers and the shared dock (core/js/flex/flex-workspace.js) adopts them.
 * Moving a node keeps its listeners, its gauges and its canvases, and every
 * getElementById the page makes still resolves.
 *
 * What stays where it was: the dialogs, the start-up overlay, the banners,
 * and #vfoRow - emptied of its two columns but kept, hidden, because site.js
 * reads the radio model and single-receiver flag from its data attributes.
 */

import { initFlexWorkspace } from '/js/flex/flex-workspace.js';
import { initFlexLayoutsAdmin, enableDialogDrag } from '/js/flex/flex-layouts-admin.js';

const IWC_FLEX_CONFIG = {
    layoutUrl: '/js/flexui/layouts/desktop.json',
    // v2: the Linear Meters panel joined the Meters tabset.
    layoutVersion: 'v2',
    storagePrefix: 'iwc.flexui.',
    apiBase: '/api/flexlayouts',
    popoutUrl: '/popout.html',
    fallbackPanel: 'vfoA',
    panels: [
        { id: 'toolbar', name: 'Toolbar', component: 'toolbar' },
        { id: 'linearMeters', name: 'Linear Meters', component: 'linearMeters' },
        { id: 'meters', name: 'Meters', component: 'meters' },
        { id: 'spectrum', name: 'Spectrum', component: 'spectrum' },
        { id: 'vfoA', name: 'VFO A', component: 'vfoA' },
        { id: 'vfoB', name: 'VFO B', component: 'vfoB' },
        { id: 'clarifier', name: 'Clarifier', component: 'clarifier' },
    ],
};

/** Each panel's section on the Classic page. Order matters - see below. */
function findSections() {
    const meterCard = document.getElementById('meterCard');
    return {
        // Fabio's bar meters: rendered on /flexui only (Index.cshtml).
        linearMeters: document.getElementById('linearMetersSection'),
        meters: meterCard?.closest('.row') || null,
        spectrum: document.getElementById('spectrumContainer'),
        vfoA: document.getElementById('vfoACol'),
        vfoB: document.getElementById('vfoBCol'),
        clarifier: document.getElementById('clarifierRow'),
        // The row straight after the connection banner. In the markup it
        // also encloses the meters, the spectrum and #vfoRow, so it is
        // wrapped last, once they have been taken out of it.
        toolbar: document.getElementById('connectionProblemBanner')?.nextElementSibling || null,
    };
}

function wrap(component, section, parking) {
    const body = document.createElement('div');
    body.className = 'ywc-panel-body iwc-flex-panel';
    body.setAttribute('data-ywc-component', component);
    body.appendChild(section);
    parking.appendChild(body);
    return body;
}

function moveSectionsIntoParking() {
    const parking = document.getElementById('ywcFlexParking');
    const source = document.getElementById('iwcFlexSource');
    const s = findSections();
    const vfoRow = document.getElementById('vfoRow');

    for (const component of ['linearMeters', 'meters', 'spectrum', 'vfoA', 'vfoB', 'clarifier']) {
        if (s[component]) wrap(component, s[component], parking);
    }
    // #vfoRow is a data holder now: keep it in the page, out of the toolbar.
    if (vfoRow && source) {
        source.appendChild(vfoRow);
        vfoRow.classList.add('iwc-flex-vfo-row');
    }
    if (s.toolbar) {
        wrap('toolbar', s.toolbar, parking);
        s.toolbar.classList.add('iwc-flex-toolbar-row');
    }

    // Shown in the Spectrum panel while the page has the scope hidden
    // (no scope stream configured, or the radio not connected yet).
    const spectrumBody = parking.querySelector('[data-ywc-component="spectrum"]');
    if (spectrumBody) {
        const note = document.createElement('p');
        note.className = 'iwc-flex-empty-note';
        note.textContent = 'The scope is not showing. It appears here once the radio is connected and its scope is streaming.';
        spectrumBody.prepend(note);
    }
}

function wireLayoutsDialog() {
    const dialog = document.getElementById('flexLayoutsDialog');
    if (!dialog) return;
    enableDialogDrag(dialog, document.getElementById('flexLayoutsHeader'));
    let admin = null;
    window.addEventListener('ywc-flex-manage-layouts', () => {
        if (!admin) {
            admin = initFlexLayoutsAdmin(document.getElementById('flexLayoutsAdmin'), {
                apiBase: IWC_FLEX_CONFIG.apiBase,
                onSelect: (id) => window.ywcFlex?.switchTo(id).then(() => admin?.refresh()),
                onChange: () => window.ywcFlex?.refreshLayouts(),
            });
        } else {
            admin.refresh();
        }
        try { dialog.showModal(); } catch { dialog.setAttribute('open', ''); }
    });
}

function start() {
    const host = document.getElementById('ywcFlexHost');
    if (!host) return;
    moveSectionsIntoParking();
    wireLayoutsDialog();
    window.addEventListener('ywc-flex-ready', () => {
        document.body.classList.remove('iwc-flex-pending');
        // Gauges and the scope size themselves to their box on resize; their
        // box has just changed from the Classic page's to the dock's.
        window.dispatchEvent(new Event('resize'));
    }, { once: true });
    initFlexWorkspace(host, { vfoB: true }, IWC_FLEX_CONFIG);
}

start();
