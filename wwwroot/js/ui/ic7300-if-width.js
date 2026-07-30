// IC-7300 IF passband filter width (CI-V 1A 03) + FIL1/2/3 slot (CI-V 26).
//
// This is the browser mirror of the server-side FilterWidthCodec in
// Services/CivRadioController.cs — the valid widths and the mode→group rule
// MUST stay in step with it. It provides:
//   - window.IfWidth.rebuildIfWidthSelect(selectEl, radioModel, mode)
//       the seam the ModeA/ModeB SignalR handlers in site.js already call, so
//       the IF-Width dropdown re-fills whenever the mode changes.
//   - window.setIfWidth(receiver, hz)      → POST /api/radio/ifwidth/{a|b}
//   - window.setFilterSlot(receiver, fil)  → POST /api/radio/filter/{a|b}
//
// Loaded after site.js, so these definitions are the ones the page uses.
(function () {
    'use strict';

    // Shared SSB/CW/RTTY curve: codes 0–9 → (c+1)*50 (50–500 Hz, 50 Hz step),
    // then codes 10+ → 600 + (c-10)*100 (100 Hz step). SSB/CW runs to code 40
    // (3.6 kHz); RTTY stops at code 31 (2.7 kHz).
    function steppedWidths(count) {
        const a = [];
        for (let c = 0; c < count; c++) a.push(c <= 9 ? (c + 1) * 50 : 600 + (c - 10) * 100);
        return a;
    }
    const SSB_CW = steppedWidths(41);                                   // 50 Hz … 3.6 kHz
    const RTTY   = steppedWidths(32);                                   // 50 Hz … 2.7 kHz
    const AM     = Array.from({ length: 50 }, (_, c) => 200 + c * 200); // 200 Hz … 10 kHz

    // Mode display string → its width list, mirroring GroupForModeByte:
    // SSB/CW (incl DATA variants) → SSB_CW; RTTY → RTTY; AM → AM;
    // FM / DATA-FM / unknown → null (no adjustable width).
    function widthsForMode(mode) {
        if (!mode) return null;
        const m = String(mode).toUpperCase();
        if (m.indexOf('FM') !== -1) return null;                 // FM, DATA-FM
        if (m.indexOf('RTTY') !== -1) return RTTY;
        if (m === 'AM') return AM;
        if (m.indexOf('CW') !== -1) return SSB_CW;               // CW-U / CW-L
        if (m === 'USB' || m === 'LSB'
            || m.indexOf('SSB') !== -1 || m.indexOf('DATA') !== -1) return SSB_CW;
        return null;
    }

    function labelFor(hz) {
        if (hz < 1000) return hz + ' Hz';
        const k = hz / 1000;
        return (Number.isInteger(k) ? k.toFixed(1) : String(k)) + ' kHz';
    }

    // Fill the <select> with the mode's width options. Hides the whole row when
    // the mode has no adjustable width (FM). Keeps the current selection if it
    // is still valid, otherwise falls back to the server-rendered data-current.
    function rebuildIfWidthSelect(selectEl, _radioModel, mode) {
        if (!selectEl) return;
        const row = selectEl.closest('.vfo-control-item');
        const widths = widthsForMode(mode);
        if (!widths) {
            if (row) row.style.display = 'none';
            selectEl.innerHTML = '';
            return;
        }
        if (row) row.style.display = '';

        const prev = selectEl.value || selectEl.getAttribute('data-current') || '';
        selectEl.innerHTML = '';
        widths.forEach(function (hz) {
            const opt = document.createElement('option');
            opt.value = String(hz);
            opt.textContent = labelFor(hz);
            selectEl.appendChild(opt);
        });
        if (prev && widths.some(function (hz) { return String(hz) === String(prev); })) {
            selectEl.value = String(prev);
        }
    }

    window.IfWidth = { rebuildIfWidthSelect: rebuildIfWidthSelect, widthsForMode: widthsForMode };

    // The ModeA/ModeB seam in site.js only fires the rebuild when
    // window._radioModel is truthy (a YWC multi-model guard). The IC-7300's
    // width table is fixed, so just satisfy the guard.
    if (!window._radioModel) window._radioModel = 'IC-7300';

    function currentMode(receiver) {
        const sel = document.getElementById('modeSelect' + receiver);
        return sel ? sel.value : '';
    }
    function initReceiver(receiver) {
        rebuildIfWidthSelect(
            document.getElementById('ifWidthSelect' + receiver),
            window._radioModel, currentMode(receiver));
    }
    document.addEventListener('DOMContentLoaded', function () {
        initReceiver('A');
        initReceiver('B');
    });

    // -- Setters → the new /api/radio endpoints ----------------------------

    window.setIfWidth = async function (receiver, hz) {
        const val = parseInt(hz, 10);
        if (!Number.isFinite(val)) return;
        try {
            await fetch('/api/radio/ifwidth/' + receiver.toLowerCase(), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ hz: val })
            });
        } catch (e) { console.error('setIfWidth error:', e); }
    };

    window.setFilterSlot = async function (receiver, fil) {
        const val = parseInt(fil, 10);
        if (!(val >= 1 && val <= 3)) return;
        // Optimistic: light the chosen slot now; the poll (SelectedFilter) confirms.
        if (window.dspSetActive) window.dspSetActive('filGroup' + receiver, String(val));
        try {
            await fetch('/api/radio/filter/' + receiver.toLowerCase(), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ fil: val })
            });
        } catch (e) { console.error('setFilterSlot error:', e); }
    };
})();
