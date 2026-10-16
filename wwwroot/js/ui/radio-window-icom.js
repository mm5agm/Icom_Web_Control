// radio-window-icom.js - IWC's feed for one of the radio panel's windows
// (CW, RTTY, Memories, Meters, RX Setup) in a browser window of its own, the
// page at /RadioWindow?g=<group>. The frame is core's (window-popout.js); the
// panels are filled by the same code as on the board
// (radio-panel-icom-windows.js), so they behave the same in both places.

import { mountWindowPopout } from '/js/radio-panel/window-popout.js';
import { windowPageApi, num } from '/js/radio-panel/window-kit.js';
import { ICOM_WINDOWS, startIcomWindows } from '/js/ui/radio-panel-icom-windows.js';

function post(url, body) {
    const init = { method: 'POST' };
    if (body !== undefined) { init.headers = { 'Content-Type': 'application/json' }; init.body = JSON.stringify(body); }
    return fetch(url, init)
        .then(res => { if (!res.ok) console.warn('radio window:', url, res.status); return res; })
        .catch(e => console.warn('radio window:', url, e));
}

/**
 * @param {HTMLElement} root
 * @param {object} cfg  { conn, popout, group, maxPowerW, maxHz }  popout: the page's PopoutChild
 */
export function startIcomRadioWindow(root, cfg) {
    const g = ICOM_WINDOWS.find(x => x.id === cfg.group && x.popout);
    if (!g) {
        root.textContent = 'No such window.';
        return;
    }
    document.title = g.title + ' - Icom Web Control';
    const pop = mountWindowPopout(root, {
        title: g.title,
        panels: g.panels,
        onReattach: () => cfg.popout?.reattach(),
        onClose: () => window.close(),
        storagePrefix: 'iwc.radioPanel.',
        fontBase: '/lib/fonts',
    });

    const r = { sel: 'A', freq: { A: 0, B: 0 } };
    const win = startIcomWindows(windowPageApi(pop, g.id), {
        maxPowerW: num(cfg.maxPowerW) || 100,
        maxHz: num(cfg.maxHz) || 74_800_000,
        post,
        selected: () => r.sel,
        freq: vfo => r.freq[vfo] ?? 0,
        popouts: false,
    });

    const conn = cfg.conn;
    conn.on('RadioStateUpdate', msg => {
        const v = msg?.value;
        switch (msg?.property) {
            case 'ActiveVfo': r.sel = num(v) === 1 ? 'B' : 'A'; break;
            case 'FrequencyA': r.freq.A = num(v); break;
            case 'FrequencyB': r.freq.B = num(v); break;
        }
        win.onRadioState(msg);
    });
    conn.start().catch(e => console.warn('radio window: hub', e));
}
