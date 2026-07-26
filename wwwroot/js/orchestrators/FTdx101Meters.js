// Yaesu Web Control – Meter Orchestrator
// Connects calibration-engine → MeterPanel.
// No DOM queries, no SignalR, no string formatting.
// Owns TX state, smoothing, noise filtering, calibration, and gauge updates.
// Returns plain numeric displayValue objects so the caller can format and update DOM labels.

export class FTdx101Meters {
    /**
     * @param {object} meterPanel        An initialised MeterPanel instance
     * @param {object} calibrationEngine An object exposing calibrateNumeric(key, raw)
     */
    constructor(meterPanel, calibrationEngine) {
        this._meterPanel   = meterPanel;
        this._calibration  = calibrationEngine;

        // TX state
        this._isTransmitting = false;

        // Smoothing: rolling-average windows for power and SWR.
        // Power uses a longer window (15 samples ≈ 1.5 s at 10 Hz polling)
        // because the PWR calibration curve gets steep above 100 W — each raw
        // ADC unit is ~1.6 W there, so even a few units of ADC noise visibly
        // jolts the gauge needle. SWR stays at 7 samples (~0.7 s) so the
        // operator sees a high SWR fault quickly enough to react.
        this._powerHistory        = [];
        this._swrHistory          = [];
        this._powerHistoryLength  = 15;
        this._swrHistoryLength    = 7;
        this._wasTransmittingPower = false;
        this._wasTransmittingSWR   = false;

        // IDD filter state
        this._iddLast      = 0;
        this._iddZeroCount = 0;

        // VDD filter state — IC-7300 idles at ~13.8 V on the PA rail.
        this._vddLast  = 13.8;
        this._vddSkips = 0;
    }

    // ----------------------------------------------------------------
    // TX state
    // ----------------------------------------------------------------

    /**
     * Notify the orchestrator that TX state has changed.
     * Must be called whenever IsTransmitting updates before the next meter update.
     * @param {boolean} value
     */
    setTransmitting(value) {
        this._isTransmitting = value;
    }

    // ----------------------------------------------------------------
    // Public entry point
    // ----------------------------------------------------------------

    /**
     * Route a single meter update from SignalR through processing to the gauge.
     *
     * @param {string} property   SignalR property name, e.g. 'PowerMeter'
     * @param {number} rawValue   Raw ADC value from the radio (0–255)
     * @returns {{ skip: boolean, gaugeKey: string, displayValue: object } | null}
     *   null   — property is not a known meter
     *   skip   — filtered/debounced reading; caller should not update DOM
     *   Otherwise displayValue contains plain numeric fields ready for formatting
     */
    handleMeterUpdate(property, rawValue) {
        switch (property) {
            case 'PowerMeter':       return this._processPower(rawValue);
            case 'SWRMeter':         return this._processSWR(rawValue);
            case 'CompressionMeter': return this._processCompression(rawValue);
            case 'ALCMeter':         return this._processALC(rawValue);
            case 'IDDMeter':         return this._processIDD(rawValue);
            case 'VDDMeter':         return this._processVDD(rawValue);
            default:                 return null;
        }
    }

    /**
     * Returns true if the given property name is handled by handleMeterUpdate.
     * @param {string} property
     */
    isMeterProperty(property) {
        return ['PowerMeter', 'SWRMeter', 'CompressionMeter', 'ALCMeter',
                'IDDMeter', 'VDDMeter'].includes(property);
    }

    // ----------------------------------------------------------------
    // Per-meter processors
    // ----------------------------------------------------------------

    _processPower(raw) {
        if (!this._isTransmitting) {
            this._powerHistory        = [];
            this._wasTransmittingPower = false;
            this._meterPanel.update('power', 0);
            return { skip: false, gaugeKey: 'power', displayValue: { watts: 0, rawAvg: 0 } };
        }
        if (!this._wasTransmittingPower) {
            this._powerHistory = [];
        }
        this._wasTransmittingPower = true;
        this._powerHistory.push(raw);
        if (this._powerHistory.length > this._powerHistoryLength) this._powerHistory.shift();
        const rawAvg      = this._powerHistory.reduce((s, v) => s + v, 0) / this._powerHistory.length;
        const watts       = this._calibration.calibrateNumeric('PWR', rawAvg);
        const clampedWatts = Math.round(Math.max(0, Math.min(watts, 100)));
        this._meterPanel.update('power', clampedWatts);
        return { skip: false, gaugeKey: 'power', displayValue: { watts: clampedWatts, rawAvg } };
    }

    _processSWR(raw) {
        if (!this._isTransmitting) {
            this._swrHistory        = [];
            this._wasTransmittingSWR = false;
            this._meterPanel.update('swr', 0);
            return { skip: false, gaugeKey: 'swr', displayValue: { swr: 1.0 } };
        }
        if (!this._wasTransmittingSWR) {
            this._swrHistory = [];
        }
        this._wasTransmittingSWR = true;
        this._swrHistory.push(raw);
        if (this._swrHistory.length > this._swrHistoryLength) this._swrHistory.shift();
        // Require at least 2 readings before displaying — single-reading bursts are likely noise.
        if (this._swrHistory.length < 2) return { skip: true };
        const rawAvg    = this._swrHistory.reduce((s, v) => s + v, 0) / this._swrHistory.length;
        const swr       = this._calibration.calibrateNumeric('SWR', rawAvg);
        const swrClamped = Math.min(swr, 10.0);
        this._meterPanel.update('swr', (swrClamped - 1.0) * 127.5);
        return { skip: false, gaugeKey: 'swr', displayValue: { swr: swrClamped } };
    }

    _processCompression(raw) {
        // IC-7300 COMP meter: raw 0=0 dB, 130=15 dB, 210=30 dB (CI-V 15 14).
        const db = this._isTransmitting
            ? Math.max(0, Math.min(30, this._calibration.calibrateNumeric('Compression', raw)))
            : 0;
        this._meterPanel.update('compression', db);
        return { skip: false, gaugeKey: 'compression', displayValue: { db } };
    }

    _processALC(raw) {
        // IC-7300 ALC is a relative 0–100 % scale, not volts: the CI-V meter
        // (15 13) reads raw 0=minimum … 120=maximum, so the calibration maps
        // raw→percent and the gauge face is 0–100 %.
        if (!this._isTransmitting) {
            this._meterPanel.update('alc', 0);
            return { skip: false, gaugeKey: 'alc', displayValue: { percent: 0, rawValue: 0 } };
        }
        const percent = Math.round(Math.max(0, Math.min(100, this._calibration.calibrateNumeric('ALC', raw))));
        this._meterPanel.update('alc', percent);
        return { skip: false, gaugeKey: 'alc', displayValue: { percent, rawValue: raw } };
    }

    _processIDD(raw) {
        if (!this._isTransmitting) {
            this._iddLast = 0;
            this._iddZeroCount = 0;
            this._meterPanel.update('idd', 0);
            return { skip: false, gaugeKey: 'idd', displayValue: { amps: 0 } };
        }
        const amps = this._calibration.calibrateNumeric('IDD', raw);
        if (amps === 0) {
            this._iddZeroCount++;
            if (this._iddZeroCount < 2) return { skip: true };
        } else {
            this._iddZeroCount = 0;
        }
        if (Math.abs(amps - this._iddLast) > 5 && this._iddLast !== 0) return { skip: true };
        this._iddLast = amps;
        this._meterPanel.update('idd', Math.max(0, Math.min(amps, 25)));
        return { skip: false, gaugeKey: 'idd', displayValue: { amps } };
    }

    _processVDD(raw) {
        // IC-7300 Vd (PA supply): raw 0=0 V, 13=10 V, 241=16 V (CI-V 15 15).
        // The rail is steady, so reject a single wild jump (>2 V) but let a
        // genuine, persistent shift through on the next reading (bounded skip).
        const volts = this._calibration.calibrateNumeric('VPA', raw);
        if (this._vddLast !== 0 && Math.abs(volts - this._vddLast) > 2 && this._vddSkips < 1) {
            this._vddSkips++;
            return { skip: true };
        }
        this._vddSkips = 0;
        this._vddLast = volts;
        this._meterPanel.update('vdd', Math.max(10, Math.min(volts, 16)));
        return { skip: false, gaugeKey: 'vdd', displayValue: { volts } };
    }
}
