// Yaesu Web Control – Calibration Engine
// Pure functions only. No DOM, no UI, no gauge logic, no side effects.
//
// This is the single source of truth for all meter calibration.
// Usage:
//   import { calibrateNumeric, calibrateSMeterLabel, loadFromBackend } from './calibration-engine.js';
//
// At startup call loadFromBackend() once to replace default tables with
// user-saved calibration data from the server.  All subsequent calls to
// calibrateNumeric / calibrateSMeterLabel will use the loaded data.

import { defaultTables } from './calibration-tables.js?v=10';

// Live tables — initialised from defaults, replaced by loadFromBackend().
// Copied so the imported defaults are never mutated.
const tables = {};
for (const [key, rows] of Object.entries(defaultTables)) {
    tables[key] = rows.map(r => ({ ...r }));
}

// ------------------------------------------------------------
// Internal helpers
// ------------------------------------------------------------

// Linear interpolation between adjacent calibration points.
function interpolate(table, raw) {
    if (raw <= table[0].raw) return table[0].value;
    if (raw >= table[table.length - 1].raw) return table[table.length - 1].value;
    for (let i = 1; i < table.length; i++) {
        if (raw <= table[i].raw) {
            const prev = table[i - 1];
            const next = table[i];
            const t = (raw - prev.raw) / (next.raw - prev.raw);
            return prev.value + t * (next.value - prev.value);
        }
    }
    return table[table.length - 1].value;
}

// Snap to the nearest lower-or-equal raw entry (used for S-meter labels).
function snapLabel(table, raw) {
    let last = table[0];
    for (const pt of table) {
        if (raw < pt.raw) break;
        last = pt;
    }
    return last.label ?? String(last.value ?? last.raw);
}

// ------------------------------------------------------------
// Public API
// ------------------------------------------------------------

/**
 * Calibrate a raw ADC meter reading to a display value.
 * Falls back to the raw value (identity) when no table exists for meterName.
 *
 * @param {string} meterName  Key matching an entry in calibration-tables.js
 *                            e.g. 'PWR', 'SWR', 'ALC', 'IDD', 'VPA', 'TPA'
 * @param {number} raw        Raw 0–255 ADC value from the radio
 * @returns {number}          Calibrated display value
 */
export function calibrateNumeric(meterName, raw) {
    const table = tables[meterName];
    if (!table || table.length === 0) return raw;
    return interpolate(table, raw);
}

/**
 * Return the S-meter label string for a raw S-meter reading.
 *
 * @param {number} raw  Raw 0–255 ADC value
 * @returns {string}    Label such as 'S7', '+10', '+40'
 */
export function calibrateSMeterLabel(raw) {
    return snapLabel(tables.SMETER_LABELS, raw);
}

/**
 * Load backend calibration data and replace the live tables.
 * Safe to call at startup; silently falls back to defaults on any error.
 *
 * The backend returns a dictionary keyed by meter name (e.g. 'S-Meter').
 * backendNameMap translates those names to the table keys used here.
 * A backend entry can target MULTIPLE local tables — needed for S-meter
 * specifically, where one backend table ('S-Meter') drives both the
 * numeric gauge scale ('SMETER') and the snap-to-nearest label set
 * ('SMETER_LABELS'). Without that, calibrating S-meter only updated the
 * snap labels and left the gauge needle position on the hardcoded
 * defaults — Jacek (SP3L) reported the symptom on #29.
 *
 * @param {Object} backendNameMap  e.g. { 'S-Meter': ['SMETER', 'SMETER_LABELS'] }.
 *                                 Value can be a string for single-target or
 *                                 an array for multi-target.
 * @returns {Promise<boolean>}     true on successful load, false on any failure
 */
export async function loadFromBackend(backendNameMap = {}) {
    try {
        const response = await fetch('/api/calibration/all');
        if (!response.ok) return false;
        const data = await response.json();
        for (const [backendName, points] of Object.entries(data)) {
            const mapped = backendNameMap[backendName] ?? backendName;
            const targetKeys = Array.isArray(mapped) ? mapped : [mapped];
            for (const key of targetKeys) {
                if (!(key in tables)) continue;
                tables[key] = points.map(p => {
                    const rawVal = p.Raw ?? p.raw ?? 0;
                    // CalibrationPoint serialises the display value as "Radio" (a numeric string).
                    // Fall back through legacy field names before using raw as identity.
                    const radioStr = p.Radio ?? p.Value ?? p.value ?? p.Label ?? p.label;
                    const value = (radioStr !== undefined && radioStr !== null && radioStr !== '')
                        ? Number(radioStr)
                        : rawVal;
                    // Preserve the original string as a label when it can't be parsed as a number
                    // (e.g. S-meter points store 'S1', 'S7', '+20' as their Radio value).
                    const label = (radioStr != null && isNaN(Number(radioStr))) ? radioStr : undefined;
                    return { raw: rawVal, value: isNaN(value) ? rawVal : value, ...(label !== undefined && { label }) };
                });
            }
        }
        return true;
    } catch (e) {
        console.warn('[CalibrationEngine] Backend load failed, using defaults:', e.message);
        return false;
    }
}
