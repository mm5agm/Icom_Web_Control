// tuning-step.js — IWC's single tuning-step instance, plus the wiring that
// keeps the four ways of setting it in agreement.
//
// The store itself is shared (core/js/tuning/tuning-step-store.js, copied into
// wwwroot/js/tuning/ at build time by CopySharedCoreJs). What lives here is
// IWC-specific: the storage key prefix, the bridge onto window for site.js (a
// classic script, not a module), the screen-reader announcement, and the
// one-way-at-a-time sync with the voice nudge step, which is a server-side
// setting rather than a browser one.
//
// Deliberately NOT seeded from the voice nudge step on load. That setting
// defaults to 10 kHz, and seeding from it would silently make the spectrum wheel
// ten times coarser for every voice user on upgrade. The wheel keeps its old
// 1 kHz default until somebody changes it; after that the two follow each other.
//
// Ported from Yaesu Web Control, where Bruce VK2RT asked for it in discussion
// #168. The store was written into core/ for exactly this: nothing had to
// change to serve a second brand of radio.

import { TuningStepStore, TUNING_STEPS, formatTuningStep }
    from '../tuning/tuning-step-store.js';

// The instance lives on `window`, not in this module, and that is deliberate.
//
// A module's identity is its full URL, query string included. Index.cshtml
// imports this file as `?v=<AppVersion>`, spectrum-panel.js imports it with no
// query at all, so the browser evaluates it TWICE and each copy would own a
// separate store. The frequency display would then set one store and the
// spectrum wheel would read the other, so clicking a digit changed the Step box
// but not the wheel -- which is exactly what happened in YWC on 2026-09-20.
// Anything stateful reached from both a Razor page and another module has this
// problem; an import map is the general cure, this is the local one.
function existingStore() {
    // Duck-typed rather than `instanceof`: a second copy of the store module
    // has its own class object, so `instanceof` would reject a perfectly good
    // store created by the other copy.
    const candidate = globalThis.window ? window.iwcTuningStep : null;
    return (candidate
        && typeof candidate.get       === 'function'
        && typeof candidate.set       === 'function'
        && typeof candidate.subscribe === 'function')
        ? candidate
        : null;
}

export const tuningStep = existingStore() ?? new TuningStepStore({
    storageKeyPrefix: 'iwc.tuningStep.',
});

// Steps the voice nudge API will accept. 1 MHz and 10 MHz are offered on the
// wheel but have no voice phrase and are rejected by the controller, so a wheel
// step that large simply doesn't propagate to the voice setting.
const VOICE_VALID_STEPS = [1, 10, 100, 1_000, 10_000, 100_000];

// Classic scripts (site.js) can't import, so expose the bits they need.
window.iwcTuningStep       = tuningStep;
window.iwcTuningSteps      = TUNING_STEPS;
window.iwcFormatTuningStep = formatTuningStep;

/**
 * Announce into site.js's shared live region (`_sr_live`, assertive). Cleared
 * first so a screen reader re-reads an identical message — stepping 1 kHz ->
 * 1 kHz can't happen, but A and B can reach the same value and the user needs
 * to hear which one moved.
 */
function announce(message) {
    const el = document.getElementById('_sr_live');
    if (!el || !message) return;
    el.textContent = '';
    requestAnimationFrame(() => { el.textContent = message; });
}

/**
 * Push a step change out to the voice nudge setting, so "tune up" moves the
 * dial by the same amount the wheel does. Skipped when the value has no voice
 * equivalent, and silent on failure so a host without voice configured is a
 * normal configuration rather than a console full of errors.
 */
async function syncVoiceNudgeStep(vfo, stepHz) {
    if (!VOICE_VALID_STEPS.includes(stepHz)) return;

    const select = document.getElementById('voiceNudgeStepSelect' + vfo);
    if (select && select.value !== String(stepHz)) select.value = String(stepHz);

    try {
        await fetch('/api/voice/nudge-step', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ stepHz, vfo }),
        });
    } catch { /* voice not present on this host */ }
}

/**
 * Bring the voice nudge step into line with the wheel step once, at load.
 *
 * The two controls are set up by different mechanisms at different moments.
 * voice-control.js is a classic script, so it runs during parse and fills its
 * dropdown from the server's saved setting; this module is deferred, so it
 * restores the wheel step from localStorage afterwards. Nothing reconciled the
 * two, and they have different defaults -- 10 kHz for the voice nudge, 1 kHz
 * for the wheel -- so a page load could perfectly well show 1 Hz on the Step
 * box and 10 kHz on the voice dropdown, with no way for the operator to tell
 * which one "tune up" was going to use. Reported by Colin on 2026-09-22.
 *
 * The wheel's value wins, for the reason given at the top of this file: it is
 * the one the operator last chose on this machine, and unlike the server
 * setting it is per-browser, so it is the one that matches what they are
 * looking at. Nothing is sent when the two already agree, so the usual load
 * costs no request at all.
 */
function reconcileVoiceNudgeStepOnLoad() {
    for (const vfo of ['A', 'B']) {
        // Server-rendered by Index.cshtml. Absent on any other page, which is
        // not an error -- there is simply no voice dropdown there to reconcile.
        const serverHz = Number(window['iwcVoiceNudgeStepHz' + vfo]);
        if (!Number.isFinite(serverHz) || serverHz <= 0) continue;

        const wheelHz = tuningStep.get(vfo);
        if (wheelHz !== serverHz) syncVoiceNudgeStep(vfo, wheelHz);
    }
}

// Guarded for the same reason the store is: a second copy of this module must
// not add a second announcement and a second voice POST for every change.
if (!window.iwcTuningStepWired) {
    window.iwcTuningStepWired = true;

    reconcileVoiceNudgeStepOnLoad();

    tuningStep.subscribe((vfo, stepHz, meta) => {
        if (meta.silent !== true) {
            announce(`VFO ${vfo} tuning step ${formatTuningStep(stepHz)}`);
        }
        // `fromVoice` marks a change that came FROM the voice dropdown, which
        // has already told the server. Echoing it back would be a redundant POST.
        if (meta.fromVoice !== true) syncVoiceNudgeStep(vfo, stepHz);
    });
}
