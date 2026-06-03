// Yaesu Web Control – Gauge Factory
// Creates gauge instances based on a type string.
// No layout logic, no DOM queries, no calibration logic.

import { SMeterGauge, PowerGauge, SWRGauge, ALCGauge, TempGauge, CompressionGauge, IDDGauge, VDDGauge } from './gauge.js';

// Registry of gauge constructors.
// Add new meter types here as your UI grows.
const gaugeRegistry = {
    smeter: SMeterGauge,
    power:  PowerGauge,
    swr:    SWRGauge,
    alc:    ALCGauge,
    temp:   TempGauge,
    compression: CompressionGauge,
    idd: IDDGauge,
    vdd: VDDGauge
};

// High-contrast colour overrides applied when Windows High Contrast mode is active.
// Canvas ignores CSS forced-colors, so we must set explicit colours manually.
const HIGH_CONTRAST_OVERRIDES = window.matchMedia('(forced-colors: active)').matches
    ? {
        colorPlate:                  'transparent',
        colorMajorTicks:             '#ffffff',
        colorMinorTicks:             '#ffffff',
        colorNumbers:                '#ffffff',
        colorNeedle:                 '#ffff00',
        colorNeedleEnd:              '#ffff00',
        colorNeedleCircleInner:      '#ffff00',
        colorNeedleCircleInnerEnd:   '#ffff00',
        colorBar:                    '#444444',
        colorBarProgress:            '#00ff00',
        colorBarProgressEnd:         '#00ff00',
        highlights:                  []
      }
    : {};

/**
 * Create a gauge instance.
 *
 * @param {string} type - The gauge type (e.g., "smeter", "power").
 * @param {string} canvasId - The ID of the canvas element.
 * @param {object} options - Gauge configuration overrides.
 * @returns {object|null} - A gauge instance or null if type is unknown.
 */
export function createGauge(type, canvasId, options = {}) {
    const Constructor = gaugeRegistry[type];

    if (!Constructor) {
        console.warn(`GaugeFactory: Unknown gauge type "${type}"`);
        return null;
    }

    return new Constructor(canvasId, Object.assign({}, HIGH_CONTRAST_OVERRIDES, options));
}

/**
 * Register a new gauge type at runtime.
 * Useful for plugins or future expansion.
 *
 * @param {string} type
 * @param {class} constructor
 */
export function registerGauge(type, constructor) {
    if (typeof constructor !== 'function') {
        console.error(`GaugeFactory: constructor for "${type}" must be a class/function`);
        return;
    }

    gaugeRegistry[type] = constructor;
}

// Named factory functions — used by Index.cshtml and any page that
// needs a specific gauge type without knowing the string key.
export function createSMeterGauge(canvasId, options = {}) { return createGauge('smeter', canvasId, options); }
export function createPowerGauge(canvasId, options = {})  { return createGauge('power',  canvasId, options); }
export function createSWRGauge(canvasId, options = {})    { return createGauge('swr',    canvasId, options); }
export function createALCGauge(canvasId, options = {})    { return createGauge('alc',    canvasId, options); }
export function createTempGauge(canvasId, options = {})   { return createGauge('temp',   canvasId, options); }
export function createCompressionGauge(canvasId, options = {}) { return createGauge('compression', canvasId, options); }
export function createIDDGauge(canvasId, options = {})    { return createGauge('idd', canvasId, options); }
export function createVDDGauge(canvasId, options = {})    { return createGauge('vdd', canvasId, options); }
