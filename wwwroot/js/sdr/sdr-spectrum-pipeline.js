// FTdx101 WebApp – SDR Spectrum Pipeline
// Transport and routing only. No DOM, no canvas, no calibration, no formatting.
//
// Creates its own SignalR connection to /radioHub and dispatches the three
// message types the spectrum display needs:
//   "SpectrumUpdate"  — FFT bin array from SdrBackgroundService
//   "SdrStatus"       — device lifecycle state ("unconfigured", "connecting",
//                       "streaming", "disconnected", "nodll")
//   "FrequencyA/B"    — VFO frequency changes so the axis labels stay correct
//
// Uses the same { property, value } message envelope as the rest of the app
// so the existing WsUpdatePipeline can be reused for dispatching.

import { WsUpdatePipeline } from '../websocket/ws-update-pipeline.js';

export class SdrSpectrumPipeline {

    constructor() {
        this._pipeline   = new WsUpdatePipeline();
        this._connection = null;
    }

    // ── Public API ─────────────────────────────────────────────────���─────────

    /**
     * Open a SignalR connection and begin routing messages.
     * Safe to call multiple times; subsequent calls are ignored.
     */
    connect() {
        if (this._connection) return;

        const conn = new window.signalR
            .HubConnectionBuilder()
            .withUrl('/radioHub')
            .withAutomaticReconnect()
            .build();

        conn.on('RadioStateUpdate', (msg) => this._pipeline.handleMessage(msg));
        conn.start().catch(() => { /* reconnect is handled by withAutomaticReconnect */ });

        this._connection = conn;
    }

    /**
     * Register a handler for spectrum bin data.
     * @param {function({ bins: number[], centreHz: number, spanHz: number })} handler
     */
    onSpectrumUpdate(handler) {
        this._pipeline.register('SpectrumUpdate', handler);
    }

    /**
     * Register a handler for SDR lifecycle status strings.
     * @param {function(string)} handler  Receives values such as "streaming", "disconnected", etc.
     */
    onStatusChange(handler) {
        this._pipeline.register('SdrStatus', handler);
    }

    /**
     * Register a handler for SDR error detail strings.
     * Fired alongside "disconnected" status to give a human-readable cause.
     * @param {function(string)} handler
     */
    onError(handler) {
        this._pipeline.register('SdrError', handler);
    }

    /**
     * Register a handler for VFO A frequency changes (Hz).
     * @param {function(number)} handler
     */
    onFrequencyA(handler) {
        this._pipeline.register('FrequencyA', (value) => handler(value));
    }

    /**
     * Register a handler for VFO B frequency changes (Hz).
     * @param {function(number)} handler
     */
    onFrequencyB(handler) {
        this._pipeline.register('FrequencyB', (value) => handler(value));
    }

    /**
     * Register a handler for new DX cluster spots.
     * Each spot is the JSON shape from /api/dxcluster/spots.
     * @param {function(object)} handler
     */
    onDxSpot(handler) {
        this._pipeline.register('DxSpot', (value) => handler(value));
    }
}
