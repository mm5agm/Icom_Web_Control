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
import { buildLevelsKeys } from '/js/flexui/iwc-keys.js';
import { buildButtonsKeys } from '/js/flexui/iwc-buttons.js';
import { initFlexLayoutsAdmin, enableDialogDrag } from '/js/flex/flex-layouts-admin.js';

const IWC_FLEX_CONFIG = {
    layoutUrl: '/js/flexui/layouts/desktop.json',
    // v3: Linear Meters is a narrow column beside the Spectrum, as on YWC.
    // v4: the toolbar is a strip above the dock; VFO Actions is a panel.
    // v5: Levels panel.
    // v6: Buttons panel.
    layoutVersion: 'v6',
    storagePrefix: 'iwc.flexui.',
    apiBase: '/api/flexlayouts',
    popoutUrl: '/popout.html',
    fallbackPanel: 'vfoA',
    panels: [
        { id: 'levels', name: 'Levels', component: 'levels' },
        { id: 'vfoActions', name: 'VFO Actions', component: 'vfoActions' },
        { id: 'buttons', name: 'Buttons', component: 'buttons' },
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
    // Fabio's VFO Actions panel (YWC _VfoActions.cshtml) takes the swap,
    // copy and split keys out of the toolbar row before it becomes the strip.
    wrap('vfoActions', buildVfoActions(), parking);
    // Fabio's Levels keys (YWC _Levels.cshtml) on IWC's own controls.
    wrap('levels', buildLevelsKeys(), parking);
    // Fabio's Buttons panel (YWC _Buttons.cshtml): ATU, Tune, VOX and the
    // panel openers leave the toolbar row for keys of their own.
    wrap('buttons', buildButtonsKeys(), parking);
    if (s.toolbar) placeToolbarStrip(s.toolbar);

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

function buildVfoActions() {
    const col = el('div', 'op-col op-col--vfo-actions');
    col.id = 'vfoActionsSection';
    return appendAll(col, ...['swapVfoBtn', 'copyBtoABtn', 'copyAtoBBtn', 'splitBtn', 'quickSplitBtn']
        .map((id) => document.getElementById(id)));
}

/**
 * The toolbar row stays in the page, above the dock, as YWC's
 * _FlexToolbar does: clock and Coffee on the left; apps, Mem, S-hist, the
 * dock's own controls, Connect and POWER on the right.
 */
function placeToolbarStrip(row) {
    row.classList.add('iwc-flex-toolbar-row');
    const dock = document.querySelector('.iwc-flex-nav .iwc-flex-toolbar');
    const connect = document.getElementById('connectBtn');
    if (dock && connect) connect.before(dock);
}

function el(tag, className) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    return node;
}

function appendAll(parent, ...nodes) {
    for (const node of nodes) if (node) parent.appendChild(node);
    return parent;
}

/**
 * Rebuild one VFO card's body as Fabio's three columns (YWC _VfoA.cshtml):
 * tune (frequency, S bar, RF/AF/squelch, band, segment, mode), filter
 * (IF width, slot, shape, APF, Twin PBT, RX Tone) and keys (voice mic, the
 * AGC/preamp/ATT/NR/NB/notch grid). The Classic controls are moved, not
 * copied, so their handlers and ids are unchanged; the status line stays
 * below the grid.
 */
function regridVfo(v) {
    const $ = (id) => document.getElementById(id + v);
    const item = (id) => $(id)?.closest('.vfo-control-item') || null;
    const body = document.getElementById(`vfo${v}Col`)?.querySelector('.card-body');
    const freqRow = $('freq')?.closest('.vfo-freq-row');
    if (!body || !freqRow || body.querySelector('.vfo-body-grid')) return;
    const oldGroup = body.querySelector('.receiver-controls-group');

    // AF gain sits in the Classic freq row with its value inside the label;
    // in the gain stack it takes the label | slider | value columns.
    const afSlider = $('afGainSlider');
    const afValue = $('afGainValue');
    let afItem = null;
    if (afSlider && afValue) {
        const oldLabel = afValue.closest('label');
        afItem = el('div', 'vfo-control-item');
        const label = el('label', 'vfo-control-label');
        label.htmlFor = afSlider.id;
        label.textContent = 'AF Gain';
        afValue.className = 'vfo-control-label';
        afSlider.style.width = '';
        appendAll(afItem, label, afSlider, afValue);
        oldLabel?.remove();
    }
    const gainStack = appendAll(el('div', 'vfo-gain-stack'), item('rfGainSlider'), afItem, $('squelchRow'));

    // APF leaves the segment row for the filter column.
    const apfSelect = $('apfSelect');
    let apfItem = null;
    if (apfSelect) {
        const oldLabel = apfSelect.previousElementSibling;
        apfItem = el('div', 'vfo-control-item');
        const label = el('label', 'vfo-control-label');
        label.htmlFor = apfSelect.id;
        label.textContent = 'APF';
        appendAll(apfItem, label, apfSelect);
        if (oldLabel?.tagName === 'SPAN') oldLabel.remove();
    }
    const segmentRow = $('segmentSelect')?.parentElement || null;
    const modeRow = $('modeSelect')?.parentElement || null;
    modeRow?.classList.remove('mt-1');
    if (modeRow) appendAll(modeRow, $('saveMemBtn')?.parentElement);

    const tune = appendAll(el('div', 'vfo-col vfo-col--tune'),
        freqRow, $('sMeterLinearRow'), gainStack,
        $('bandRadioGroup')?.closest('.band-buttons-container'), segmentRow, modeRow);
    const band = appendAll(el('div', 'vfo-col vfo-col--band'),
        $('ifWidthRow'), item('filGroup'), item('ifShapeGroup'), apfItem,
        $('audioFilterBtn')?.parentElement);
    const keys = appendAll(el('div', 'vfo-col vfo-col--keys'),
        $('voiceMicContainer'), $('agcGroup')?.closest('.vfo-controls-column'));

    body.prepend(appendAll(el('div', 'vfo-body-grid'), tune, band, keys));
    // Only the emptied Classic wrappers are left in it.
    oldGroup?.remove();
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
    regridVfo('A');
    regridVfo('B');
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
