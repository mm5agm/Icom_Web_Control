// Icom Web Control - keyboard shortcuts for the home page.
//
// The help dialog, the typing guard, key matching and the capture listener
// are shared with YWC in core/js/keys/shortcut-shell.js. What is here is the
// Icom half: which key does what to this page's controls. The key map
// follows YWC's so an operator moving between the two apps keeps the same
// fingers, minus what IWC has no control for (IF shift is Twin PBT here,
// and there is no Radio Scope, Radio Display or Remote Audio on this page).
//
// Do not import freq-keyboard.js / memories.js: Index loads them with a ?v=
// cache-buster, and a second, unversioned import would be a separate module
// instance with its own DOM references. Their buttons are clicked instead.
// For the same reason Index passes autoModeForHz in: its band-plan.js
// instance is the one the server's band plan was loaded into.
import {
    createAnnouncer, createShortcutHelp, installShortcuts, isCode,
    resolveBracketTuneStepHz, resolveTuneStepHz,
} from '../keys/shortcut-shell.js';

const MIN_FREQ_HZ = 30_000;
const MAX_FREQ_HZ = 74_800_000;   // IC-7300 receive range tops out at 74.8 MHz

const announce = createAnnouncer('kbShortcutAnnounce');
let autoModeForHz = () => null;

/**
 * The configured browser TX key (Settings), if any. A shortcut never acts
 * on it: site.js toggles transmit on that key in the bubble phase, and does
 * not look at defaultPrevented, so a shortcut on the same key would fire as
 * well as the transmit, not instead of it.
 */
function txToggleKeyMatches(e) {
    const k = window.iwcTxToggleKey;
    if (k == null || k === '') return false;
    if (k === 'Space' || k === ' ') return e.key === ' ';
    return k.length === 1 && e.key.length === 1 ? e.key.toLowerCase() === k.toLowerCase() : e.key === k;
}

/** @type {import('../keys/shortcut-shell.js').ShortcutHelpRow[]} */
export const SHORTCUT_HELP = [
    { group: 'Frequency & VFO', keys: 'g', action: 'Open the frequency keypad for the active VFO' },
    { group: 'Frequency & VFO', keys: 'j / ←', action: 'Frequency down 100 Hz' },
    { group: 'Frequency & VFO', keys: 'i / →', action: 'Frequency up 100 Hz' },
    { group: 'Frequency & VFO', keys: 'Shift + j/i/←/→', action: 'Step 1 kHz' },
    { group: 'Frequency & VFO', keys: 'Alt + j/i/←/→', action: 'Step 10 kHz' },
    { group: 'Frequency & VFO', keys: '[ / ]', action: 'Step 1 Hz' },
    { group: 'Frequency & VFO', keys: 'Shift + [ / ]', action: 'Step 50 Hz' },
    { group: 'Frequency & VFO', keys: 'Shift + Alt + j/i', action: 'Tune to the previous / next DX spot' },
    { group: 'Frequency & VFO', keys: 'n', action: 'Switch the active VFO (A ↔ B)' },
    { group: 'Frequency & VFO', keys: 'N', action: 'Copy VFO A to B (A = B)' },
    { group: 'Frequency & VFO', keys: 'm', action: 'Open / close the memories panel' },
    { group: 'Frequency & VFO', keys: 'b / B', action: 'Previous / next band' },
    { group: 'Mode', keys: 'l', action: 'LSB' },
    { group: 'Mode', keys: 'u', action: 'USB' },
    { group: 'Mode', keys: 'c', action: 'CW-U' },
    { group: 'Mode', keys: 'C or Alt+c', action: 'CW-L' },
    { group: 'Mode', keys: 'a', action: 'AM' },
    { group: 'Mode', keys: 'q', action: 'FM' },
    { group: 'Mode', keys: 'd', action: 'DATA-U' },
    { group: 'Mode', keys: 'Alt+d', action: 'DATA-L' },
    { group: 'Passband', keys: 'p', action: 'Narrower IF width (one step)' },
    { group: 'Passband', keys: 'P', action: 'Wider IF width (one step)' },
    { group: 'Passband', keys: '/', action: 'Next filter slot (FIL1 → FIL2 → FIL3)' },
    { group: 'Spectrum', keys: 'z / Z', action: 'Narrower / wider span', when: 'scope showing' },
    { group: 'Spectrum', keys: 'Alt + z / Alt + Z', action: 'Narrowest / widest span', when: 'scope showing' },
    { group: 'Spectrum', keys: '< / >', action: 'Wider / narrower span', when: 'scope showing' },
    { group: 'Spectrum', keys: 'w / W', action: 'Vertical range −/+ 1 dB (Alt: 10 dB)', when: 'scope showing' },
    { group: 'Spectrum', keys: 's', action: 'Hold / release the spectrum', when: 'scope showing' },
    { group: 'Spectrum', keys: 'S', action: 'Reset the vertical range to 60 dB', when: 'scope showing' },
    { group: 'Panels', keys: 'D', action: 'Open / close the DX Spots list' },
    { group: 'Panels', keys: '@', action: 'Open the DX Watch list' },
    { group: 'Panels', keys: 'x', action: 'Show / hide the VFO B panel' },
    { group: 'Panels', keys: 'y', action: 'Show / hide the S-meter history strip' },
    { group: 'Panels', keys: 'f / F', action: 'Full screen' },
    { group: 'Help', keys: '? or h', action: 'Open this list' },
    { group: 'Help', keys: 'Esc', action: 'Close this list or a dialog; leave full screen' },
];

function helpRows() {
    const k = window.iwcTxToggleKey;
    if (k == null || k === '') return SHORTCUT_HELP;
    // Say which key is taken, so nobody wonders why its shortcut is dead.
    return [...SHORTCUT_HELP, {
        group: 'Help', keys: k === 'Space' || k === ' ' ? 'Space' : k,
        action: 'Transmit on / off (set in Settings) - no shortcut acts on this key',
    }];
}

/** Our mode strings, as the Index mode dropdown carries them. */
export function resolveModeShortcut(key, mods = {}) {
    if (!key || key.length !== 1) return null;
    const lower = key.toLowerCase();
    const alt = !!mods.altKey;
    if (lower === 'l') return 'LSB';
    if (lower === 'u') return 'USB';
    if (lower === 'c') return (alt || key === 'C') ? 'CW-L' : 'CW-U';
    if (key === 'a') return 'AM';
    if (key === 'q') return 'FM';
    if (key === 'd') return alt ? 'DATA-L' : 'DATA-U';
    return null;
}

// ── Page state ───────────────────────────────────────────────────────────────

function activeVfo() {
    const v = typeof window.getActiveVfoLetter === 'function' ? window.getActiveVfoLetter() : null;
    return v === 'B' ? 'B' : 'A';
}

function isFrequencyDisplayFocused() {
    const a = document.activeElement;
    return !!a?.closest?.('#freqA, #freqB, #freqA-controls, #freqB-controls');
}

/** Arrow keys belong to a focused radiogroup, slider or field, not to tuning. */
function arrowsOwnedByFocus() {
    const a = document.activeElement;
    if (!a) return false;
    const role = a.getAttribute?.('role');
    return role === 'radio' || role === 'slider' || !!a.closest?.('[role="radiogroup"]')
        || a.tagName === 'INPUT' || a.tagName === 'SELECT';
}

function currentFrequencyHz(vfo) {
    const hz = window.radioControl?._state?.lastBackendFreq?.[vfo];
    if (Number.isFinite(hz) && hz > 0) return hz;
    const digits = Array.from(document.querySelectorAll(`#freq${vfo} .digit`))
        .map(d => d.textContent).filter(t => /\d/.test(t)).join('');
    const n = parseInt(digits, 10);
    return Number.isFinite(n) ? n : 0;
}

function stepFrequency(deltaHz) {
    const vfo = activeVfo();
    const cur = currentFrequencyHz(vfo);
    if (!cur || !window.radioControl?.setFrequency) return;
    const next = Math.max(MIN_FREQ_HZ, Math.min(MAX_FREQ_HZ, Math.round(cur + deltaHz)));
    if (next !== cur) window.radioControl.setFrequency(vfo, next);
}

// ── Actions ──────────────────────────────────────────────────────────────────

function cycleBand(direction) {
    const vfo = activeVfo();
    const labels = Array.from(document.querySelectorAll(`#bandRadioGroup${vfo} label[data-band]`));
    if (!labels.length) return;
    const cur = document.querySelector(`#bandRadioGroup${vfo} input[type="radio"]:checked`)?.value?.toLowerCase();
    let idx = labels.findIndex(l => l.dataset.band.toLowerCase() === cur);
    if (idx < 0) idx = direction > 0 ? -1 : 0;
    const next = labels[(idx + direction + labels.length) % labels.length];
    next.click();
    announce(`Band ${next.dataset.band}`);
}

function setModeForActive(mode) {
    const vfo = activeVfo();
    const select = document.getElementById(`modeSelect${vfo}`);
    if (select && !Array.from(select.options).some(o => o.value === mode)) {
        announce(`Mode ${mode} not available`);
        return;
    }
    if (select) select.value = mode;
    window.setMode?.(vfo, mode);
    announce(`Mode ${select?.selectedOptions[0]?.textContent?.trim() || mode}`);
}

function cycleIfWidth(direction) {
    const vfo = activeVfo();
    const select = document.getElementById(`ifWidthSelect${vfo}`);
    if (!select || select.disabled || !select.options.length) {
        announce('IF width not available in this mode');
        return;
    }
    const idx = select.selectedIndex;
    const next = Math.max(0, Math.min(select.options.length - 1, idx + direction));
    if (next === idx) return;
    select.selectedIndex = next;
    window.setIfWidth?.(vfo, select.value);
    announce(`IF width ${select.options[next].textContent.trim()}`);
}

function nextFilterSlot() {
    const vfo = activeVfo();
    const buttons = Array.from(document.querySelectorAll(`#filGroup${vfo} .dsp-seg`));
    if (!buttons.length) return;
    const idx = buttons.findIndex(b => b.classList.contains('active'));
    const next = buttons[(idx + 1) % buttons.length];
    next.click();
    announce(next.textContent.trim());
}

function toggleActiveVfo() {
    const next = activeVfo() === 'A' ? 'B' : 'A';
    window.setActiveVfo?.(next);
    announce(`VFO ${next}`);
}

function copyAtoB() {
    if (typeof window.copyVfo === 'function') window.copyVfo('ab');
    else document.getElementById('copyAtoBBtn')?.click();
    announce('VFO A copied to B');
}

function toggleMemories() {
    const dialog = document.getElementById('memoriesDialog');
    if (dialog?.open) document.getElementById('memoriesClose')?.click();
    else document.getElementById('memBtn')?.click();
}

function toggleDxSpots() {
    // Popped out, the list is in its own window: dxSpotsOpen brings it forward.
    if (!document.getElementById('dxSpotsDialog')?.open && typeof window.dxSpotsOpen === 'function') {
        window.dxSpotsOpen();
        return;
    }
    window.dxSpotsPanel?.toggle?.();
}

function openDxWatch() {
    const dlg = document.getElementById('dxWatchDialog');
    if (!dlg) return;
    dlg.show();
    window.refreshDxWatchList?.();
}

// ── Spectrum ─────────────────────────────────────────────────────────────────

function spectrumTarget() {
    const shown = vfo => {
        const el = document.getElementById(`spectrumContainer${vfo}`);
        return !!el && el.style.display !== 'none' && el.getClientRects().length > 0;
    };
    const vfo = activeVfo();
    if (shown(vfo) && window[`spectrumPanel${vfo}`]) return { vfo, panel: window[`spectrumPanel${vfo}`] };
    const other = vfo === 'A' ? 'B' : 'A';
    if (shown(other) && window[`spectrumPanel${other}`]) return { vfo: other, panel: window[`spectrumPanel${other}`] };
    return null;
}

function cycleSpan(ctx, direction, extreme) {
    // Buttons are ordered narrow → wide in the markup.
    const buttons = Array.from(document.querySelectorAll(`.span-btn[data-vfo="${ctx.vfo}"]`));
    if (!buttons.length) return;
    const idx = buttons.findIndex(b => b.classList.contains('active'));
    const nextIdx = extreme
        ? (direction < 0 ? 0 : buttons.length - 1)
        : Math.max(0, Math.min(buttons.length - 1, (idx < 0 ? (direction < 0 ? buttons.length : -1) : idx) + direction));
    if (nextIdx === idx) return;
    buttons[nextIdx].click();
    announce(`Span ${buttons[nextIdx].textContent.trim()}`);
}

function setSpectrumRange(ctx, db) {
    if (!ctx.panel.setSpectrumRange) return;
    const next = Math.max(5, Math.min(140, Math.round(db)));
    ctx.panel.setSpectrumRange(next);
    const slider = document.querySelector(`.spectrum-range-slider[data-vfo="${ctx.vfo}"]`);
    const label = document.querySelector(`.spectrum-range-label[data-vfo="${ctx.vfo}"]`);
    if (slider) slider.value = String(next);
    if (label) label.textContent = `${next} dB`;
    announce(`Spectrum range ${next} dB`);
}

function stepDxSpot(direction) {
    const vfo = activeVfo();
    const panel = window[`spectrumPanel${vfo}`] || window.spectrumPanelA;
    const spots = (panel?._spots || []).filter(s => Number.isFinite(s.frequencyHz))
        .sort((a, b) => a.frequencyHz - b.frequencyHz);
    if (!spots.length) {
        announce('No DX spots');
        return;
    }
    const cur = currentFrequencyHz(vfo);
    const target = direction > 0
        ? (spots.find(s => s.frequencyHz > cur + 50) || spots[0])
        : ([...spots].reverse().find(s => s.frequencyHz < cur - 50) || spots[spots.length - 1]);
    if (!window.radioControl?.setFrequency) return;
    window.radioControl.setFrequency(vfo, target.frequencyHz);
    const mode = autoModeForHz(target.frequencyHz);
    if (mode) window.setMode?.(vfo, mode);
    announce(`DX ${target.callsign || ''} ${(target.frequencyHz / 1000).toFixed(1)}`.trim());
}

// ── Dispatcher ───────────────────────────────────────────────────────────────

/**
 * @param {KeyboardEvent} e
 * @returns {boolean} true if a shortcut acted (the shell then prevents default)
 */
export function handleKey(e) {
    const key = e.key;
    if (txToggleKeyMatches(e)) return false;
    // f/F full screen is site.js's own listener; leave it there.
    if (key === 'f' || key === 'F') return false;

    const mode = resolveModeShortcut(key, e);
    if (mode) {
        if (!e.repeat) setModeForActive(mode);
        return true;
    }
    if (key === 'D' && !e.altKey) { toggleDxSpots(); return true; }
    if (key === '@') { openDxWatch(); return true; }
    if (key === 'g' && !e.altKey) { document.querySelector(`[data-freq-keyboard="${activeVfo()}"]`)?.click(); return true; }
    if (isCode(e, 'KeyM') && !e.altKey && !e.shiftKey) { toggleMemories(); return true; }
    if (key === 'n' && !e.altKey) { toggleActiveVfo(); return true; }
    if (key === 'N' && !e.altKey) { copyAtoB(); return true; }
    if ((key === 'b' || key === 'B') && !e.altKey) { cycleBand(key === 'B' ? 1 : -1); return true; }

    if ((isCode(e, 'BracketLeft') || isCode(e, 'BracketRight')) && !e.altKey) {
        const step = resolveBracketTuneStepHz(e);
        stepFrequency(isCode(e, 'BracketRight') ? step : -step);
        return true;
    }

    const arrow = key === 'ArrowLeft' || key === 'ArrowRight';
    const down = isCode(e, 'KeyJ') || key === 'ArrowLeft';
    const up = isCode(e, 'KeyI') || key === 'ArrowRight';
    if (down || up) {
        // The frequency display uses ←/→ to pick a digit.
        if (arrow && (isFrequencyDisplayFocused() || arrowsOwnedByFocus())) return false;
        if (e.shiftKey && e.altKey && !arrow) {
            stepDxSpot(up ? 1 : -1);
            return true;
        }
        const step = resolveTuneStepHz(e);
        if (step > 0) {
            stepFrequency(up ? step : -step);
            return true;
        }
        return false;
    }

    if (key === 'p' && !e.altKey) { cycleIfWidth(-1); return true; }
    if (key === 'P' && !e.altKey) { cycleIfWidth(1); return true; }
    if (isCode(e, 'Slash') && !e.shiftKey && !e.altKey) { nextFilterSlot(); return true; }

    const spec = spectrumTarget();
    if (spec) {
        if (isCode(e, 'KeyZ')) { cycleSpan(spec, e.shiftKey ? 1 : -1, e.altKey); return true; }
        if (key === '<' || key === '>' || ((isCode(e, 'Comma') || isCode(e, 'Period')) && e.shiftKey)) {
            const wider = key === '<' || isCode(e, 'Comma');
            cycleSpan(spec, wider ? 1 : -1, false);
            return true;
        }
        if (isCode(e, 'KeyW')) {
            const step = (e.altKey ? 10 : 1) * (e.shiftKey ? 1 : -1);
            setSpectrumRange(spec, (spec.panel.getSpectrumRange?.() ?? 60) + step);
            return true;
        }
        if (isCode(e, 'KeyS') && !e.altKey) {
            if (e.shiftKey) setSpectrumRange(spec, 60);
            else document.querySelector(`.spectrum-hold-btn[data-vfo="${spec.vfo}"]`)?.click();
            return true;
        }
    }

    if (key === 'x' && !e.altKey) { document.getElementById('vfoBToggleBtn')?.click(); return true; }
    if (key === 'y' && !e.altKey) { document.getElementById('sMeterHistoryToggleBtn')?.click(); return true; }
    return false;
}

/** @param {{ autoModeForHz?: (hz: number) => string | null }} deps */
export function initKeyboardShortcuts(deps = {}) {
    if (typeof deps.autoModeForHz === 'function') autoModeForHz = deps.autoModeForHz;
    const help = createShortcutHelp({
        rows: helpRows,
        intro: 'Shortcuts act on the active VFO. They are ignored while typing in a text field, '
            + 'the CW Send box or the frequency keypad, and while this list is open the arrow keys '
            + 'scroll it. Browser chords such as Ctrl+F are never captured.',
    });
    installShortcuts({ help, handleKey, textEntryDialogIds: ['freqKeyboardDialog'] });
}
