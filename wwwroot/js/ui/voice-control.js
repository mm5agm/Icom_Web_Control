// Voice control frontend (Step 4 of v1 plan).
//
// Wires the navbar mic button to /api/voice/start and /api/voice/stop on
// mousedown / mouseup (touch equivalents too). Listens for VoiceStatusUpdate
// over SignalR and reflects the recogniser state in the button colour +
// last-heard text.
//
// Step 5 will add a Settings-page toggle. This module reads
// window.ywcVoiceEnabled at init (server-rendered) to decide whether to
// show the button at all.

(function () {
    'use strict';

    const btn = document.getElementById('voiceMicBtn');
    const lastHeardEl = document.getElementById('voiceLastHeard');
    if (!btn) return;

    // Show button only if voice control is enabled in Settings.
    // Step 5 sets window.ywcVoiceEnabled from server-side @Model. For
    // Step 4 the flag is undefined and the button stays hidden so users
    // don't see a non-functional control until Settings exposes the
    // toggle.
    if (!window.ywcVoiceEnabled) return;
    btn.style.display = '';

    // ---- Visual state ----------------------------------------------------

    function setButtonClass(suffix) {
        btn.className = 'btn btn-sm btn-' + suffix + ' mx-2';
    }
    function setIdleVisual()        { setButtonClass('outline-secondary'); }
    function setListeningVisual()   { setButtonClass('primary'); }
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

    // ---- PTT plumbing ----------------------------------------------------

    let pressed = false;

    async function startListening() {
        if (pressed) return;
        pressed = true;
        setListeningVisual();
        try {
            await fetch('/api/voice/start', { method: 'POST' });
        } catch (e) {
            console.error('[voice] start failed', e);
            setErrorVisual();
            pressed = false;
        }
    }

    async function stopListening() {
        if (!pressed) return;
        pressed = false;
        try {
            await fetch('/api/voice/stop', { method: 'POST' });
        } catch (e) {
            console.error('[voice] stop failed', e);
        }
        // Visual state settles back to idle when SignalR delivers the
        // Idle status update (after the engine drains any pending audio).
    }

    btn.addEventListener('mousedown', startListening);
    btn.addEventListener('touchstart', function (e) { e.preventDefault(); startListening(); });

    // mouseup / mouseleave on the button is fine; but if the user drags the
    // mouse OFF the button while held, we want to keep listening until they
    // release. So listen on document for mouseup.
    document.addEventListener('mouseup', stopListening);
    document.addEventListener('touchend', stopListening);
    document.addEventListener('touchcancel', stopListening);

    // ---- SignalR status updates -----------------------------------------

    // The site.js module owns the SignalR connection lifecycle. It exposes
    // window.signalRConnection (or similar) once connected. To avoid
    // coupling, we just listen for the VoiceStatusUpdate event on whichever
    // connection appears on window.
    function tryAttachSignalR() {
        const conn = window.signalRConnection || window.connection;
        if (!conn || typeof conn.on !== 'function') {
            setTimeout(tryAttachSignalR, 250);
            return;
        }
        conn.on('VoiceStatusUpdate', handleStatusUpdate);
    }
    tryAttachSignalR();

    function handleStatusUpdate(status) {
        // status: { state, lastHeard, lastIntent, errorMessage }
        // The state name comes through as the C# enum name (PascalCase).
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
            btn.title = 'Hold to give a voice command';
        }
    }

    // Pull initial status once so the button reflects server state on
    // page load (e.g. Error if en-GB SAPI is missing).
    fetch('/api/voice/status').then(r => r.ok ? r.json() : null).then(s => {
        if (s) handleStatusUpdate(s);
    }).catch(() => {});
})();
