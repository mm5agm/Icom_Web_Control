// Voice control frontend.
//
// Wires each VFO's mic button (Index.cshtml: voiceMicBtnA / voiceMicBtnB) to
// /api/voice/start?vfo=A|B and /api/voice/stop on mousedown / mouseup (touch
// equivalents too). Listens for VoiceStatusUpdate over SignalR and reflects
// the recogniser state in that VFO's button colour + last-heard text.
//
// Only one SAPI recognition session can be live at a time (single
// SpeechRecognitionEngine on the backend), so the two buttons are
// independent controls over one shared engine, not two engines. Each
// VoiceStatusUpdate carries a `vfo` field identifying which button's press
// is being reported; a button ignores updates that aren't its own so the
// other button doesn't flicker.
//
// window.iwcVoiceEnabled (server-rendered in _Layout.cshtml) gates whether
// any mic button is shown at all. VFO B's button is additionally hidden on
// single-receiver radios via #vfoRow's data-single-receiver attribute.

(function () {
    'use strict';

    if (!window.iwcVoiceEnabled) return;

    function createVfoVoiceControl(vfo, defaultStepHz) {
        const suffix    = vfo; // 'A' | 'B'
        const container = document.getElementById('voiceMicContainer' + suffix);
        const btn        = document.getElementById('voiceMicBtn' + suffix);
        const stepSelect = document.getElementById('voiceNudgeStepSelect' + suffix);
        const lastHeardEl = document.getElementById('voiceLastHeard' + suffix);
        if (!btn || !container) return null;

        container.style.display = '';

        if (stepSelect) {
            stepSelect.value = String(defaultStepHz || 10000);
            stepSelect.addEventListener('change', async function () {
                const stepHz = parseInt(this.value, 10);
                try {
                    await fetch('/api/voice/nudge-step', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({ stepHz, vfo }),
                    });
                } catch (e) {
                    console.error('[voice] nudge-step update failed (VFO ' + vfo + ')', e);
                }
            });
        }

        // ---- Visual state --------------------------------------------

        function setButtonClass(styleSuffix) {
            btn.className = 'btn btn-' + styleSuffix + ' btn-sm';
        }
        function setIdleVisual()        { setButtonClass('outline-danger'); }
        function setListeningVisual()   { setButtonClass('danger'); }
        function setHeardVisual()       { setButtonClass('success'); }
        function setExecutingVisual()   { setButtonClass('success'); }
        function setErrorVisual()       { setButtonClass('danger'); }
        function setUnrecognisedVisual(){ setButtonClass('warning'); }

        function setLastHeard(text, intent) {
            if (!lastHeardEl) return;
            if (!text) { lastHeardEl.textContent = ''; return; }
            lastHeardEl.textContent = intent
                ? '“' + text + '” → ' + intent
                : '“' + text + '”';
        }

        // ---- PTT plumbing ----------------------------------------------

        let pressed = false;

        async function startListening() {
            if (pressed) return;
            pressed = true;
            setListeningVisual();
            try {
                await fetch('/api/voice/start?vfo=' + encodeURIComponent(vfo), { method: 'POST' });
            } catch (e) {
                console.error('[voice] start failed (VFO ' + vfo + ')', e);
                setErrorVisual();
                pressed = false;
            }
        }

        async function stopListening() {
            if (!pressed) return;
            pressed = false;
            // Reset the visual locally as the user releases the button rather
            // than waiting for SignalR's Idle broadcast, which may not reach
            // this client (connection blip, race with the .on() handler
            // attaching, etc.) and would otherwise leave the button lit.
            setIdleVisual();
            try {
                await fetch('/api/voice/stop', { method: 'POST' });
            } catch (e) {
                console.error('[voice] stop failed (VFO ' + vfo + ')', e);
            }
        }

        btn.addEventListener('mousedown', startListening);
        btn.addEventListener('touchstart', function (e) { e.preventDefault(); startListening(); });

        // mouseup/touchend is listened on document (not just the button) so
        // that dragging the pointer off the button while held still stops
        // listening on release rather than sticking in the Listening state.
        document.addEventListener('mouseup', function () { if (pressed) stopListening(); });
        document.addEventListener('touchend', function () { if (pressed) stopListening(); });
        document.addEventListener('touchcancel', function () { if (pressed) stopListening(); });

        function handleStatusUpdate(status) {
            // status: { state, lastHeard, lastIntent, errorMessage, vfo }
            // Ignore broadcasts for the other VFO's mic button.
            if (status.vfo && status.vfo !== vfo) return;

            switch (status.state) {
                case 'Idle':         setIdleVisual();        break;
                case 'Listening':    setListeningVisual();   break;
                case 'Heard':        setHeardVisual();       break;
                case 'Executing':    setExecutingVisual();   break;
                case 'Unrecognised': setUnrecognisedVisual();break;
                case 'Error':        setErrorVisual();       break;
                default:             /* leave as-is */       break;
            }
            if (status.lastHeard) {
                setLastHeard(status.lastHeard, status.lastIntent);
            }
            if (status.state === 'Error' && status.errorMessage) {
                btn.title = 'Voice error: ' + status.errorMessage;
            } else if (status.state === 'Listening') {
                btn.title = 'Listening… release to stop';
            } else {
                btn.title = 'Hold to give a VFO ' + vfo + ' voice command';
            }
        }

        return { vfo, stepSelect, handleStatusUpdate };
    }

    const controls = [
        createVfoVoiceControl('A', window.iwcVoiceNudgeStepHzA),
        createVfoVoiceControl('B', window.iwcVoiceNudgeStepHzB),
    ].filter(Boolean);

    // VFO B's mic button exists in the DOM even on single-receiver radios
    // (it's rendered unconditionally, like the rest of the VFO B controls)
    // but #vfoRow hides the whole VFO B panel via CSS in that case, so no
    // extra gating is needed here.

    if (controls.length === 0) return;

    // ---- SignalR status + nudge-step sync ---------------------------------

    // The site.js module owns the SignalR connection lifecycle. It exposes
    // window.signalRConnection (or similar) once connected. To avoid
    // coupling, we just listen for events on whichever connection appears on
    // window.
    function tryAttachSignalR() {
        const conn = window.signalRConnection || window.connection;
        if (!conn || typeof conn.on !== 'function') {
            setTimeout(tryAttachSignalR, 250);
            return;
        }
        conn.on('VoiceStatusUpdate', function (status) {
            controls.forEach(function (c) { c.handleStatusUpdate(status); });
        });
        conn.on('RadioStateUpdate', function (update) {
            // IntentDispatcher broadcasts VoiceNudgeStepHzA/B when a "set
            // step" voice command changes it, so the dropdown stays in sync
            // even though the change didn't come from this client.
            controls.forEach(function (c) {
                if (update.property === 'VoiceNudgeStepHz' + c.vfo && c.stepSelect) {
                    c.stepSelect.value = String(update.value);
                }
            });
        });
    }
    tryAttachSignalR();

    // Pull initial status once so each button reflects server state on page
    // load (e.g. Error if the configured SAPI locale is missing).
    fetch('/api/voice/status').then(r => r.ok ? r.json() : null).then(s => {
        if (s) controls.forEach(function (c) { c.handleStatusUpdate(s); });
    }).catch(() => {});
})();
