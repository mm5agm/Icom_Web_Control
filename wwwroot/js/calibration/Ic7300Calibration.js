// Ic7300Calibration.js
// DEPRECATED — superseded by calibration-engine.js + calibration-tables.js.
// This file is no longer loaded or called. Kept for reference only.
// Do not add new code here.

class Ic7300Calibration {
    static _calibrationData = null;
    static _cache = {};

    // Loads and caches calibration data from a JSON file or object
    static async load(calibrationSource) {
        if (typeof calibrationSource === 'string') {
            // Assume URL or path
            const response = await fetch(calibrationSource);
            this._calibrationData = await response.json();
        } else {
            // Assume object
            this._calibrationData = calibrationSource;
        }
        this._cache = {};
    }

    // Normalizes and returns calibration points for a given meter
    static getPoints(meterName) {
        if (!this._calibrationData) throw new Error('Calibration data not loaded');
        if (this._cache[meterName]) return this._cache[meterName];
        const meter = this._calibrationData.meters.find(m => m.name === meterName);
        if (!meter) throw new Error(`No calibration for meter: ${meterName}`);
        // Normalize: always use 'label' for display, and ensure raw is a number
        const points = meter.points.map(pt => {
            let label = pt.label || pt.Radio || pt.radio || pt.Label;
            // Parse raw as number
            let rawNum = typeof pt.raw === 'number' ? pt.raw : parseFloat(pt.raw);
            // Parse label as number if possible, else keep as string
            let labelNum = parseFloat(label);
            let useLabel = !isNaN(labelNum) ? labelNum : label;
            return { raw: rawNum, label: useLabel };
        });
        this._cache[meterName] = { type: meter.type, points };
        return this._cache[meterName];
    }

    // Returns the calibrated label for a given meter and raw value
    static getLabel(meterName, rawValue) {
        const { type, points } = this.getPoints(meterName);
        if (type === 's_meter') {
            // Find closest lower or equal raw, or lowest
            let last = points[0];
            for (const pt of points) {
                if (rawValue < pt.raw) break;
                last = pt;
            }
            return last.label;
        } else {
            // Numeric: interpolate between points
            for (let i = 1; i < points.length; ++i) {
                if (rawValue < points[i].raw) {
                    const prev = points[i-1];
                    const next = points[i];
                    const t = (rawValue - prev.raw) / (next.raw - prev.raw);
                    // Interpolate label if numeric, else use prev
                    const prevNum = parseFloat(prev.label);
                    const nextNum = parseFloat(next.label);
                    if (!isNaN(prevNum) && !isNaN(nextNum)) {
                        const interp = prevNum + t * (nextNum - prevNum);
                        return interp.toFixed(1);
                    }
                    return prev.label;
                }
            }
            // Above highest: use last
            return points[points.length-1].label;
        }
    }
}

window.Ic7300Calibration = Ic7300Calibration;
