// spectrum-tune-icom.js - where a click on the radio panel's scope tunes,
// for the board (radio-panel-icom.js) and the pop-out spectrum
// (radio-spectrum-icom.js), so a click does the same in both.
//
// Classic's rule: the band plan may pick the mode (unless Settings turns
// that off). IWC's Classic tunes to the clicked frequency in every mode.

import { autoModeForHz } from '/js/ui/band-plan.js';

export const MODES = ['LSB', 'USB', 'CW-U', 'CW-L', 'RTTY-L', 'RTTY-U', 'AM', 'FM', 'DATA-L', 'DATA-U'];

/** { mode: the band plan's mode, or null to leave it; to: the dial Hz }. */
export function spectrumTarget(hz) {
    const auto = autoModeForHz(hz);
    return { mode: auto && MODES.includes(auto) ? auto : null, to: Math.round(hz) };
}
