/**
 * Experimental UI (/flexui): Fabio Valente's Levels keys (YWC _Levels.cshtml)
 * on IWC's own controls.
 *
 * Each key stands in for a Classic control that is still on the page, parked
 * out of sight beside it: moving a key's slider sets the Classic slider and
 * fires its input/change events, and clicking an on/off key clicks the Classic
 * button, so every write goes through the handlers Classic already uses.
 * The other way, the keys follow the Classic value labels and button classes,
 * which IWC's hub handlers keep current - with { silent: true }, so following
 * the radio never writes back to it.
 */

import { CycleContextButton } from '/js/keys/toggle-dropdown-button.js';

const $ = (id) => document.getElementById(id);

function debounce(fn, ms) {
    let timer = null;
    return (...args) => {
        clearTimeout(timer);
        timer = setTimeout(() => fn(...args), ms);
    };
}

function setSlider(slider, value) {
    slider.value = value;
    slider.dispatchEvent(new Event('input', { bubbles: true }));
    slider.dispatchEvent(new Event('change', { bubbles: true }));
}

function watch(nodes, fn) {
    const obs = new MutationObserver(fn);
    for (const node of nodes) {
        if (node) obs.observe(node, { childList: true, characterData: true, subtree: true, attributes: true, attributeFilter: ['class'] });
    }
}

const num = (el) => {
    const n = parseInt(el?.textContent ?? '', 10);
    return Number.isFinite(n) ? n : null;
};

/**
 * One key. `slider` is the Classic range input; `label` the Classic value
 * text; `toggle` (optional) the Classic on/off button and `isOn` how to read
 * it.
 */
function levelKey(root, { label, slider, valueLabel, toggle, isOn, suffix = '', a11yKey }) {
    if (!root || !slider) return null;
    const onOff = !!toggle;
    const write = debounce((v) => setSlider(slider, v), 150);
    let last = { selectedId: onOff && isOn() ? '1' : '0', value: Number(slider.value) };

    const widget = new CycleContextButton(root, {
        label,
        options: onOff ? [{ id: '0', label: 'OFF' }, { id: '1', label }] : [{ id: 'v', label }],
        selectedId: onOff ? last.selectedId : 'v',
        offId: onOff ? '0' : null,
        showLed: onOff,
        context: {
            type: 'slider',
            min: Number(slider.min), max: Number(slider.max), step: Number(slider.step) || 1,
            value: last.value, valueSuffix: suffix,
        },
        a11yKey,
        onChange: (state) => {
            if (onOff && state.selectedId !== last.selectedId) {
                last.selectedId = state.selectedId;
                if ((state.selectedId === '1') !== isOn()) toggle.click();
            }
            if (state.value !== undefined && state.value !== last.value) {
                last.value = state.value;
                write(state.value);
            }
        },
    });

    const follow = () => {
        const value = num(valueLabel) ?? Number(slider.value);
        const partial = { value };
        if (onOff) partial.selectedId = isOn() ? '1' : '0';
        last = { selectedId: partial.selectedId ?? last.selectedId, value };
        widget.setState(partial, { silent: true });
    };
    watch([valueLabel, toggle], follow);
    return widget;
}

/** Build the Levels panel section. Returns it for the dock to adopt. */
export function buildLevelsKeys() {
    const section = document.createElement('div');
    section.id = 'levelsSection';
    section.className = 'op-col op-col--levels';
    const keyRoot = (id) => {
        const div = document.createElement('div');
        div.id = id;
        section.appendChild(div);
        return div;
    };
    const roots = {
        power: keyRoot('powerButton'),
        mic: keyRoot('micGainButton'),
        proc: keyRoot('procButton'),
        mon: keyRoot('monitorButton'),
    };

    // Look everything up now: once moved into this (not yet attached)
    // section, getElementById can no longer find them.
    const c = Object.fromEntries(['powerSlider', 'powerValue', 'micGainSlider', 'micGainValue', 'micGainLabel', 'procBtn',
        'procLevelSlider', 'procLevelValue', 'monitorOnBtn', 'monLevelSlider', 'monLevelValue'].map((id) => [id, $(id)]));

    // The Classic controls stay alive, hidden, inside the panel.
    const classic = document.createElement('div');
    classic.className = 'iwc-flex-levels-classic';
    classic.hidden = true;
    const row = c.powerSlider?.parentElement;
    for (const id of ['powerSlider', 'micGainSlider', 'procBtn', 'procLevelSlider', 'monitorOnBtn', 'monLevelSlider']) {
        const el = c[id];
        if (!el) continue;
        // Take each control's label with it (it holds the value text).
        const prev = el.previousElementSibling;
        if (prev?.tagName === 'LABEL') classic.appendChild(prev);
        classic.appendChild(el);
    }
    section.appendChild(classic);
    if (!row) return section;

    window.powerCycleButton = levelKey(roots.power, {
        label: 'POWER', slider: c.powerSlider, valueLabel: c.powerValue, suffix: ' W', a11yKey: 'controls.txPower',
    });
    const mic = levelKey(roots.mic, {
        label: 'MIC', slider: c.micGainSlider, valueLabel: c.micGainValue, a11yKey: 'controls.micGain',
    });
    window.micGainCycleButton = mic;
    // In the data modes Classic relabels MIC Gain as Data Out Gain; so does the key.
    const micLabel = c.micGainLabel;
    if (mic && micLabel) {
        const relabel = () => mic.setLabel(/data/i.test(micLabel.textContent) ? 'DATA OUT' : 'MIC');
        watch([micLabel], relabel);
        relabel();
    }
    window.procCycleButton = levelKey(roots.proc, {
        label: 'PROC', slider: c.procLevelSlider, valueLabel: c.procLevelValue,
        toggle: c.procBtn, isOn: () => c.procBtn.classList.contains('btn-success'), a11yKey: 'controls.proc',
    });
    window.monitorCycleButton = levelKey(roots.mon, {
        label: 'MONI', slider: c.monLevelSlider, valueLabel: c.monLevelValue,
        toggle: c.monitorOnBtn, isOn: () => c.monitorOnBtn.classList.contains('btn-warning'),
    });
    return section;
}
