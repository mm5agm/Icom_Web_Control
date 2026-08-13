// Icom Web Control – Spectrum Panel
// UI module — DOM access is intentional and correct here.
// Owns a single <canvas> element that is divided into two rendering zones:
//   Top 45%  — spectrum trace (line graph of dBFS vs frequency)
//   Bottom 55% — waterfall  (scrolling time–frequency heatmap)
//
// Frequency axis labels are computed from the VFO frequency reported by
// SdrSpectrumPipeline so the display is always centred on the current band.

import { modeForHz } from '../ui/band-plan.js';

export class SpectrumPanel {

    /**
     * @param {string} canvasId       ID of the <canvas> element to render into.
     * @param {string} containerId    ID of the wrapper element to show/hide.
     * @param {number} initialVfoHz   Starting VFO frequency in Hz.
     * @param {string} vfo            "A" or "B" — which VFO this panel represents.
     *                                Click-to-tune and wheel-tune target the
     *                                /api/cat/frequency/{a|b} endpoint accordingly.
     */
    constructor(canvasId, containerId, initialVfoHz = 14_074_000, vfo = 'A') {
        this._canvasId    = canvasId;
        this._containerId = containerId;
        this._vfo         = (vfo === 'B') ? 'B' : 'A';   // normalise + default
        this._vfoLower    = this._vfo.toLowerCase();      // "a"/"b" for URL paths
        this._vfoHz       = initialVfoHz;
        this._status      = 'unconfigured';

        // Waterfall state: ImageData that is scrolled down one row per frame.
        this._waterfallData = null;
        this._waterfallRows = 0;
        this._waterfallCols = 0;

        // Waterfall scroll speed — 1 scrolls a row every incoming FFT frame
        // (full speed, the original fixed behaviour); higher values skip
        // frames so the waterfall scrolls slower, e.g. 4 = one row every 4th
        // frame. The spectrum trace above it still updates every frame — this
        // only throttles the waterfall's fall-through rate. Requested by a
        // reporter who found the default too fast to read at a glance.
        // Persisted per-VFO like the split ratio.
        this._waterfallSpeed = this._loadWaterfallSpeed();
        this._waterfallFrameCounter = 0;

        // Waterfall brightness — a dB offset added to each bin before it is
        // mapped to a thermal colour. 0 = no lift (raw Low/High window); higher
        // values push weak signals further up the colour scale so a dark
        // waterfall brightens. This is YWC's old "gain" brightening as a
        // dedicated slider, independent of the spectrum trace. Persisted
        // per-VFO like the scroll speed and split ratio.
        this._wfBrightDb = this._loadWaterfallBrightness();

        this._errorDetail = null;

        // Last received spectrum data; held so the canvas can be redrawn on resize.
        // This is the *averaged* trace (see _applyAveraging) — the raw IC-7300
        // sweep is too noisy frame-to-frame to display directly.
        this._lastBins    = null;
        this._lastCentreHz = 0;
        this._lastSpanHz   = 0;

        // Two-stage spectrum smoothing, because the raw IC-7300 sweep is both
        // jittery in time *and* grassy across frequency:
        //   1. Temporal (EMA) — blend each sweep into a running average to kill
        //      frame-to-frame flicker. _avgBins is that average; _specAvgWeight is
        //      the new-frame weight (1 = off, lower = smoother/slower).
        //   2. Spatial — a short moving average across adjacent bins that turns the
        //      noise-floor grass into a smooth curve. _specSmoothRadius is the
        //      half-window (0 = off, 2 = 5-bin window). Kept separate from the EMA
        //      so it doesn't compound over successive frames. _smoothOut is the
        //      reused output buffer.
        // Both reset cleanly on a retune so the old band never ghosts into the new.
        this._avgBins         = null;
        this._specAvgWeight   = SpectrumPanel.DEFAULT_SPEC_AVG;
        this._smoothOut       = null;
        this._specSmoothRadius = SpectrumPanel.DEFAULT_SPEC_SMOOTH;

        // DX cluster spots overlaid on the spectrum. Each entry is the JSON
        // shape from /api/dxcluster/spots — { callsign, frequencyHz, spotter,
        // comment, receivedUtc }. Only spots within the current span are
        // drawn; clicks within a few pixels of a spot QSY to it precisely.
        this._spots = [];

        // DX cluster connection status — shown as a small badge in the
        // top-right of the spectrum canvas. Updated by setDxStatus().
        this._dxStatus = 'off';
        this._dxDetail = '';

        // Crosshair state — null when mouse is outside the canvas.
        this._crosshairX  = null;
        this._crosshairY  = null;

        // Persistent cursor: a "bookmarked" frequency the operator dropped
        // with Shift+click. Stays on the spectrum until cleared even as
        // the user tunes elsewhere — useful for marking a station you want
        // to come back to. null when no cursor is set.
        this._pinnedCursorHz = null;

        // Band-plan segment data for marker overlay (CW / FT8 / SSB / RTTY etc.
        // tick marks under the spectrum). Set via setBandPlan(); shape is the
        // region-specific subset of BAND_PLANS, e.g. BAND_PLANS.Region1.
        this._bandPlan = null;

        this._resizeObserver = null;

        // Spectrum/waterfall split ratio — fraction of canvas height given to
        // the spectrum trace (top zone); remainder goes to the waterfall.
        // Persisted per-VFO in localStorage so the operator can give the
        // spectrum more vertical room for low-signal work without losing
        // the setting on reload.
        this._splitRatio = this._loadSplitRatio();
        this._splitterDragging = false;

        // dB range — vertical scale of the spectrum trace, in dBFS. These are the
        // actual render window; with auto-floor on (below) they are recomputed
        // every sweep. Default fallback only, used until the first frame arrives.
        this._dbMin = -120;
        this._dbMax = 0;

        // Auto noise-floor tracking. The IC-7300 scope streams a fixed 0–160
        // amplitude trace, so the meaningful thing to control is how tall the
        // *peaks* are, not where the absolute floor sits. With auto-floor on we
        // measure the noise floor each sweep and pin it a few dB above the bottom
        // edge; the operator's single "Range" control then sets how much dB of
        // headroom sits above it (smaller = taller peaks). This removes the old
        // Floor/Range coupling where widening the span slid the noise off-screen —
        // the floor now follows the noise no matter what Range is.
        //   _autoFloorDb  — the EMA-smoothed measured noise floor (null until the
        //                   first frame, and reset on retune so it snaps to the
        //                   new band rather than drifting across).
        //   _rangeDb      — dB span from the pinned floor to the top of the scale.
        this._autoFloor   = true;
        this._autoFloorDb = null;
        this._rangeDb     = SpectrumPanel.DEFAULT_SPEC_RANGE;

        // Garbage-collect the pre-v2.4.0 client-side dB-range persistence.
        // The server now owns this via /api/sdr/dsp/{vfo}; the old key would
        // just sit forever in users' browsers otherwise. Safe to remove
        // unconditionally — removeItem on a missing key is a no-op.
        try { localStorage.removeItem('iwc.spectrumDbRange.' + this._vfo); }
        catch (e) { /* localStorage may be unavailable */ }

        this._init();
    }

    // Load persisted split ratio for this VFO. Clamped to a reasonable band
    // so a corrupt value can't make either zone vanish entirely.
    _loadSplitRatio() {
        try {
            const raw = localStorage.getItem('iwc.spectrumSplit.' + this._vfo);
            const v = parseFloat(raw);
            if (isFinite(v) && v >= 0.15 && v <= 0.85) return v;
        } catch (e) { /* localStorage may be unavailable */ }
        return 0.45;
    }

    _saveSplitRatio() {
        try {
            localStorage.setItem('iwc.spectrumSplit.' + this._vfo, this._splitRatio.toFixed(3));
        } catch (e) { /* localStorage may be unavailable */ }
    }

    // Valid waterfall speed divisors — 1 (full speed) through 128 (1/128
    // speed). Index into this array (not the divisor itself) is what the
    // UI slider in Index.cshtml drags across, since the divisors themselves
    // aren't evenly spaced.
    static WATERFALL_SPEEDS = [1, 2, 4, 8, 16, 32, 64, 128];

    // Default temporal-averaging weight — the fraction of each new sweep blended
    // into the running average. Lower = calmer/slower. 1 disables it. Kept fairly
    // responsive (0.7) because the spatial stage below removes the grass, so the
    // EMA only has to take the edge off flicker rather than carry all the smoothing.
    static DEFAULT_SPEC_AVG = 0.7;

    // Default spatial-smoothing half-window, in bins. 0 = off; 2 = a 5-bin moving
    // average, enough to smooth the noise-floor grass without visibly widening
    // real signals across the 475-point sweep.
    static DEFAULT_SPEC_SMOOTH = 2;

    // Auto noise-floor parameters:
    //   DEFAULT_SPEC_RANGE   — dB of headroom above the pinned floor (the "Range"
    //                          slider's default); ~60 dB shows band noise plus
    //                          strong signals without clipping SSB peaks.
    //   AUTOFLOOR_PERCENTILE — which order-statistic of the sweep is taken as the
    //                          noise floor. Most bins are noise, so a low
    //                          percentile lands in it while ignoring signal peaks.
    //   AUTOFLOOR_MARGIN_FRAC— where the noise floor is parked, as a fraction of
    //                          the spectrum height up from the bottom edge. A
    //                          *fraction* (not a fixed dB) is what keeps the floor
    //                          in the same spot at any Range — a fixed dB margin
    //                          would shrink to a smaller slice as Range grows and
    //                          the floor would sink. 0.15 sits it just above the
    //                          frequency-axis labels, near the top of the waterfall.
    //   AUTOFLOOR_SMOOTH     — EMA weight for the floor estimate; low = calm scale
    //                          that doesn't bounce as the noise wanders sweep-to-sweep.
    static DEFAULT_SPEC_RANGE    = 60;
    static AUTOFLOOR_PERCENTILE  = 0.15;
    static AUTOFLOOR_MARGIN_FRAC = 0.05;
    static AUTOFLOOR_SMOOTH      = 0.1;

    // Height in px reserved at the bottom of the spectrum zone for the frequency-
    // axis labels. The trace maps into the area above this; _drawFrequencyAxis
    // paints its strip into it. Kept as one constant so the two never disagree.
    static AXIS_H = 20;

    _loadWaterfallSpeed() {
        try {
            const v = parseInt(localStorage.getItem('iwc.waterfallSpeed.' + this._vfo), 10);
            if (SpectrumPanel.WATERFALL_SPEEDS.includes(v)) return v;
        } catch (e) { /* localStorage may be unavailable */ }
        return 1;
    }

    _saveWaterfallSpeed() {
        try {
            localStorage.setItem('iwc.waterfallSpeed.' + this._vfo, String(this._waterfallSpeed));
        } catch (e) { /* localStorage may be unavailable */ }
    }

    /**
     * Set how many incoming FFT frames the waterfall waits between scrolling
     * a new row — 1 = every frame (full speed), 4 = one row every 4th frame
     * (1/4 speed), etc. The spectrum trace is unaffected; it always updates
     * on every frame. Invalid values are ignored.
     * @param {number} divisor  One of SpectrumPanel.WATERFALL_SPEEDS.
     */
    setWaterfallSpeed(divisor) {
        const d = parseInt(divisor, 10);
        if (!SpectrumPanel.WATERFALL_SPEEDS.includes(d)) return;
        this._waterfallSpeed = d;
        this._waterfallFrameCounter = 0;
        this._saveWaterfallSpeed();
    }

    // ── View geometry ────────────────────────────────────────────────────────
    //
    // Every overlay maps Hz→x across the canvas, and they must all agree on
    // which slice of spectrum the canvas is showing. That slice is NOT always
    // centred on the VFO:
    //
    //   Centre mode  — the radio sweeps around the VFO, so the sweep centre and
    //                  the VFO are the same number and nothing changes.
    //   Fixed/scroll — the sweep is a band segment that stays put while the VFO
    //                  moves inside it. The waveform header carries the segment's
    //                  own lower/upper edges, which CivScopeAssembler averages
    //                  into the sweep's centreHz.
    //
    // Drawing everything from _vfoHz pinned the VFO to the middle of the canvas
    // and slid the scale under it, which is exactly what a Fixed-mode operator
    // sees as "the scope shifts to keep the cursor centred". Read the centre
    // from the sweep and Fixed mode draws where the radio says it is.

    /**
     * Centre frequency of the window currently on the canvas, in Hz. Falls back
     * to the VFO until a sweep has reported a centre.
     */
    _viewCentreHz() {
        return this._lastCentreHz > 0 ? this._lastCentreHz : this._vfoHz;
    }

    /**
     * Frequency at the left edge of the canvas, in Hz.
     * @param {number} [spanHz] Span to use; defaults to the last sweep's.
     */
    _viewLeftHz(spanHz = this._lastSpanHz) {
        return this._viewCentreHz() - spanHz / 2;
    }

    /**
     * True when the window tracks the VFO, so tuning re-centres the display.
     * Fixed and the two scroll modes leave the window where it is.
     */
    _viewFollowsVfo() {
        return this._scopeMode === undefined || this._scopeMode === 'CENT';
    }

    /** Returns the current waterfall speed divisor (1 = full speed). */
    getWaterfallSpeed() { return this._waterfallSpeed; }

    // Waterfall brightness range, in dB of lift. 0 = off; MAX chosen so a
    // signal sitting on the noise floor can be pushed near full colour.
    static WATERFALL_BRIGHT_MAX = 60;

    _loadWaterfallBrightness() {
        try {
            const v = parseInt(localStorage.getItem('iwc.waterfallBright.' + this._vfo), 10);
            if (isFinite(v) && v >= 0 && v <= SpectrumPanel.WATERFALL_BRIGHT_MAX) return v;
        } catch (e) { /* localStorage may be unavailable */ }
        return 0;
    }

    _saveWaterfallBrightness() {
        try {
            localStorage.setItem('iwc.waterfallBright.' + this._vfo, String(this._wfBrightDb));
        } catch (e) { /* localStorage may be unavailable */ }
    }

    /**
     * Set the waterfall brightness lift, in dB (0 … WATERFALL_BRIGHT_MAX).
     * Only the waterfall's colour mapping is affected — the spectrum trace is
     * unchanged. Takes effect on the next scrolled-in rows (history keeps its
     * existing colours, same as a scroll-speed change).
     * @param {number} db  dB of lift; clamped to the valid range.
     */
    setWaterfallBrightness(db) {
        const v = parseInt(db, 10);
        if (!isFinite(v)) return;
        this._wfBrightDb = Math.max(0, Math.min(SpectrumPanel.WATERFALL_BRIGHT_MAX, v));
        this._saveWaterfallBrightness();
    }

    /** Returns the current waterfall brightness lift in dB (0 = off). */
    getWaterfallBrightness() { return this._wfBrightDb; }

    /**
     * Set the spectrum dB range. Called by the Low/High slider handler in
     * Index.cshtml whenever the user moves a slider. Persistence is handled
     * server-side via /api/sdr/dsp/{vfo}; this method just rescales the
     * canvas y-axis so the visual change is immediate.
     * @param {number} dbMax  Top of the scale (High slider value).
     * @param {number} dbMin  Bottom of the scale (Low slider value).
     */
    setDbRange(dbMax, dbMin) {
        if (!isFinite(dbMax) || !isFinite(dbMin) || dbMax <= dbMin) return;
        this._dbMax = dbMax;
        this._dbMin = dbMin;
        if (this._lastBins) this._render();
    }

    /**
     * Set the vertical scale height in dB (the "Range" control) while auto-floor
     * keeps the noise pinned near the bottom. Smaller = taller peaks (more gain);
     * larger = flatter. The floor never moves as a result — that's the whole point
     * of auto-floor. Out-of-range values are ignored.
     * @param {number} db  dB of headroom above the tracked noise floor.
     */
    setSpectrumRange(db) {
        const r = parseFloat(db);
        if (!isFinite(r) || r < 5 || r > 160) return;
        this._rangeDb = r;
        // Re-apply against the current floor so the change is visible immediately,
        // without waiting for the next sweep.
        if (this._autoFloorDb != null) this._applyAutoFloorWindow();
        if (this._lastBins) this._render();
    }

    /** Returns the current vertical-scale height in dB (the Range control). */
    getSpectrumRange() { return this._rangeDb; }

    /**
     * Track the noise floor from a smoothed sweep and set the render window to
     * [floor − margin, floor − margin + range]. A low percentile of the bins is a
     * robust noise-floor estimate (most bins are noise); it is EMA-smoothed so the
     * scale stays calm as the noise wanders. No-op when auto-floor is off.
     * @param {ArrayLike<number>} bins  The smoothed sweep.
     */
    _updateAutoFloor(bins) {
        if (!this._autoFloor || !bins || !bins.length) return;
        // Float32Array.sort() is numeric by default (unlike Array.prototype.sort).
        const sorted = Float32Array.from(bins).sort();
        const idx = Math.min(sorted.length - 1,
            Math.max(0, Math.floor(sorted.length * SpectrumPanel.AUTOFLOOR_PERCENTILE)));
        const measured = sorted[idx];
        if (this._autoFloorDb == null) this._autoFloorDb = measured;
        else this._autoFloorDb += SpectrumPanel.AUTOFLOOR_SMOOTH * (measured - this._autoFloorDb);
        this._applyAutoFloorWindow();
    }

    // Set the render window [dbMin, dbMax] from the tracked floor and Range so the
    // noise floor lands at a *fixed screen fraction* (AUTOFLOOR_MARGIN_FRAC) up
    // from the bottom, independent of Range. Because the trace height fraction is
    // (v − dbMin)/range, parking the floor at fraction f means dbMin = floor − f·range;
    // Range then only stretches the peaks above it, never the floor's position.
    _applyAutoFloorWindow() {
        this._dbMin = this._autoFloorDb - SpectrumPanel.AUTOFLOOR_MARGIN_FRAC * this._rangeDb;
        this._dbMax = this._dbMin + this._rangeDb;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /** Update the spectrum/waterfall with a new frame of FFT data. */
    update({ bins, centreHz, spanHz, mode }) {
        // Hold mode (set via setHold(true)) freezes the display at the
        // last received frame so the operator can inspect a fleeting signal
        // without it scrolling off the waterfall. Incoming frames are
        // dropped — _lastBins / _lastCentreHz / _lastSpanHz are not
        // updated so a forced re-render shows the frozen frame.
        if (this._hold) return;
        // Smooth this sweep before anything else uses it, so the trace, waterfall
        // and crosshair readout all see the calmed data. Stage 1 (temporal) must
        // see the span change *before* _lastSpanHz is updated, to detect a retune.
        const retuned  = (spanHz !== this._lastSpanHz);
        const averaged = this._applyAveraging(bins, spanHz);   // temporal (EMA)
        this._lastBins    = this._spatialSmooth(averaged);     // spatial (moving avg)
        // Auto noise-floor: recompute the vertical window from the tracked floor.
        // Snap (don't drift) to the new band's floor on a retune.
        if (retuned) this._autoFloorDb = null;
        this._updateAutoFloor(this._lastBins);
        this._lastCentreHz = centreHz;
        this._lastSpanHz   = spanHz;
        // Radio scope mode (CENT / FIX / SCROLL-C / SCROLL-F) for the corner
        // badge, read from the 27 00 waveform header. Left undefined until a
        // sweep carrying it arrives, which the badge treats as "nothing to
        // show" rather than guessing a mode.
        if (mode !== undefined) {
            const modeChanged = mode !== this._scopeMode;
            this._scopeMode = mode;
            // Let the page label its Centre/Fixed button from the mode the radio
            // is actually in, rather than from what we last asked for.
            if (modeChanged && typeof this.onScopeModeChange === 'function') {
                this.onScopeModeChange(mode);
            }
        }

        // Only scroll the waterfall every Nth frame per _waterfallSpeed;
        // the spectrum trace above it still redraws every frame regardless.
        const shouldScrollWaterfall = (this._waterfallFrameCounter % this._waterfallSpeed) === 0;
        this._waterfallFrameCounter++;

        this._render(shouldScrollWaterfall);
    }

    /**
     * Exponential moving average of the incoming sweep. Returns the (mutated-in-
     * place) running-average buffer. Re-seeds — i.e. shows the raw sweep with no
     * lag — on the first frame, a bin-count change, or a span change (retune), so
     * the previous band never bleeds into the new display.
     * @param {number[]} bins    Raw sweep bins (dBFS-ish).
     * @param {number}   spanHz  Span of this sweep; compared to the previous
     *                           frame's span to detect a retune.
     * @returns {Float32Array} The averaged trace.
     */
    _applyAveraging(bins, spanHz) {
        const a = this._specAvgWeight;
        if (a >= 1 || !this._avgBins
            || this._avgBins.length !== bins.length
            || spanHz !== this._lastSpanHz) {
            this._avgBins = Float32Array.from(bins);
            return this._avgBins;
        }
        const avg = this._avgBins;
        for (let i = 0; i < bins.length; i++)
            avg[i] += a * (bins[i] - avg[i]);
        return avg;
    }

    /**
     * Stage 2: spatial smoothing — a moving average of half-window
     * _specSmoothRadius across adjacent bins, into a reused output buffer so the
     * temporal average (stage 1) is left untouched. Radius 0 passes through.
     * @param {ArrayLike<number>} src  The temporally-averaged bins.
     * @returns {ArrayLike<number>} The spatially-smoothed trace.
     */
    _spatialSmooth(src) {
        const r = this._specSmoothRadius;
        if (r <= 0) return src;
        const n = src.length;
        let out = this._smoothOut;
        if (!out || out.length !== n) out = this._smoothOut = new Float32Array(n);
        for (let i = 0; i < n; i++) {
            const lo = i - r < 0 ? 0 : i - r;
            const hi = i + r >= n ? n - 1 : i + r;
            let sum = 0;
            for (let j = lo; j <= hi; j++) sum += src[j];
            out[i] = sum / (hi - lo + 1);
        }
        return out;
    }

    /**
     * Set the temporal-averaging weight (0 < w ≤ 1). Lower = smoother/slower;
     * 1 disables averaging (raw sweeps). Out-of-range values are ignored.
     * @param {number} weight
     */
    setSpectrumAveraging(weight) {
        const w = parseFloat(weight);
        if (!isFinite(w) || w <= 0 || w > 1) return;
        this._specAvgWeight = w;
    }

    /** Returns the current temporal-averaging weight (1 = averaging off). */
    getSpectrumAveraging() { return this._specAvgWeight; }

    /**
     * Set the spatial-smoothing half-window in bins (0 = off). Values are
     * floored to an integer; negatives are ignored.
     * @param {number} radius
     */
    setSpectrumSmoothing(radius) {
        const r = parseInt(radius, 10);
        if (!isFinite(r) || r < 0) return;
        this._specSmoothRadius = r;
    }

    /** Returns the current spatial-smoothing half-window (0 = off). */
    getSpectrumSmoothing() { return this._specSmoothRadius; }

    /**
     * Freeze (true) or resume (false) the display. While held the spectrum
     * and waterfall stop scrolling and the panel ignores incoming sweeps.
     * Click-to-tune, wheel-tune, and cursor tracking still work — only
     * automatic updates are paused.
     * @param {boolean} hold
     */
    setHold(hold) {
        this._hold = !!hold;
        // Force a re-render so the "Held" overlay (if any) shows immediately.
        if (this._lastBins) this._render();
    }

    /** Returns the current hold state (true = frozen, false = streaming live). */
    isHeld() { return !!this._hold; }

    /**
     * The scope mode the radio last reported ('CENT', 'FIX', 'SCROLL-C',
     * 'SCROLL-F'), or undefined until a sweep carries one. Undefined means
     * "not known yet", not "Centre" — don't let a control claim otherwise.
     */
    get scopeMode() { return this._scopeMode; }

    /**
     * Ask the radio for Centre (true) or Fixed (false) scope mode. The panel
     * doesn't set _scopeMode itself: the badge and any button follow the mode
     * the next sweep reports, so a refused change never shows as applied.
     * @param {boolean} center
     */
    setScopeMode(center) {
        fetch(`/api/cat/scopemode/${center ? 'center' : 'fixed'}`, { method: 'POST' })
            .catch(() => { /* ignore network errors */ });
    }

    /**
     * Flip between Centre and Fixed. The single entry point behind both the
     * corner badge and the panel header's Centre/Fixed button. Anything that
     * isn't Centre (including the two scroll modes) goes back to Centre, which
     * is the mode the frequency axis is built around.
     */
    toggleScopeMode() {
        this.setScopeMode(this._scopeMode !== 'CENT' ? true : false);
    }

    /** Store the latest error detail string for display alongside status overlays. */
    setError(detail) {
        this._errorDetail = detail;
        // "blocked" is a standing condition, not a blip — the operator has to go
        // and change a radio menu before anything will ever arrive. If we are
        // already sitting on that overlay, repaint so the reason appears without
        // waiting for another status announce.
        if (this._status === 'blocked') this._drawStatusOverlay(this._status);
    }

    /**
     * Update the DX cluster connection status. Drives the small badge in
     * the top-right of the spectrum canvas.
     * @param {string} status  "off" | "connecting" | "connected" | "disconnected"
     * @param {string} detail  Optional human-readable detail / error message
     */
    setDxStatus(status, detail) {
        this._dxStatus = status || 'off';
        this._dxDetail = detail || '';
        if (this._lastBins) this._render();
    }

    /**
     * Provide the region-specific band plan so the spectrum can draw marker
     * ticks at the standard CW / FT8 / SSB / RTTY activity centres. The data
     * is the region subset of BAND_PLANS (e.g. BAND_PLANS.Region1), keyed by
     * band name → { CW: {freq, label}, FT8: ..., ... }.
     */
    setBandPlan(planForRegion) {
        this._bandPlan = planForRegion || null;
        if (this._lastBins) this._render();
    }

    /**
     * Provide region-specific band edges (lo/hi frequency limits per band).
     * Used to draw the red dashed guard-rail lines on the spectrum.
     * Falls back to the class-static SpectrumPanel.BAND_EDGES (worldwide
     * broadest envelope) if not set.
     * @param {Array<{name:string, lo:number, hi:number}>} edgesForRegion
     */
    setBandEdges(edgesForRegion) {
        this._bandEdges = Array.isArray(edgesForRegion) ? edgesForRegion : null;
        if (this._lastBins) this._render();
    }

    /**
     * Force a re-render with the existing spectrum/state — used when an
     * external setting (e.g. the "only watched" filter toggle in the DX Watch
     * popup) changes and we want the overlay to update immediately rather
     * than wait for the next FFT frame.
     */
    redraw() {
        if (this._lastBins) this._render();
    }

    /** Replace the full DX spots array (used on page load when fetching /api/dxcluster/spots). */
    setSpots(spots) {
        this._spots = Array.isArray(spots) ? spots.slice() : [];
        if (this._lastBins) this._render();
    }

    /** Add or update a single spot — used when SignalR pushes a new DxSpot. */
    addSpot(spot) {
        if (!spot || !spot.callsign) return;
        // De-duplicate by callsign + frequency: a re-spot replaces the older entry.
        const i = this._spots.findIndex(s =>
            s.callsign === spot.callsign && Math.abs(s.frequencyHz - spot.frequencyHz) < 100);
        if (i >= 0) this._spots[i] = spot;
        else        this._spots.unshift(spot);
        // Cap memory: don't accumulate more than 500 spots client-side.
        if (this._spots.length > 500) this._spots.length = 500;
        if (this._lastBins) this._render();
    }

    /** Receive the current VFO frequency so the axis labels stay accurate. */
    setVfoFrequency(hz) {
        this._vfoHz = hz;
        if (this._lastBins) this._render();
        // Keep data-reading on the canvas current so the hover live region can announce it.
        const canvas = document.getElementById(this._canvasId);
        if (canvas) canvas.dataset.reading = 'centred on ' + (hz / 1e6).toFixed(6) + ' MHz';
    }

    /**
     * Respond to scope lifecycle state changes.
     * @param {string} status  One of: "unconfigured" | "connecting" | "streaming"
     *                                 | "disconnected" | "outofrange"
     */
    setStatus(status) {
        this._status = status;

        const container = document.getElementById(this._containerId);
        if (!container) return;

        // "unconfigured" used to hide the card outright — inherited from YWC,
        // where it meant "no SDR device for this VFO", a permanent condition. On
        // IWC the trace comes from the radio's own band scope, so it means
        // "the scope isn't streaming", which is usually just "it's switched
        // off" — and the Scope switch lives inside this very card. Hiding it
        // trapped the operator on a blank page (GitHub #1). Show the card and
        // let the overlay say so instead; Index.cshtml's
        // updateContainerVisibility() runs immediately after this and still has
        // the last word on which panels belong on screen.
        container.style.display = '';
        this._drawStatusOverlay(status);
    }

    // ── Initialisation ───────────────────────────────────────────────────────

    _init() {
        const container = document.getElementById(this._containerId);
        if (container) container.style.display = 'none';   // hidden until streaming

        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;

        // Size the canvas to match its CSS layout width.
        this._sizeCanvas(canvas);

        // Rebuild waterfall buffer whenever the canvas is resized.
        this._resizeObserver = new ResizeObserver(() => {
            this._sizeCanvas(canvas);
            this._waterfallData = null;  // force rebuild on next frame
            if (this._lastBins) this._render();
        });
        this._resizeObserver.observe(canvas.parentElement ?? canvas);

        // Tune VFO A to the clicked frequency.
        canvas.addEventListener('click', (e) => this._onCanvasClick(e));

        // Mouse-wheel tunes VFO A up/down in 1 kHz steps.
        // { passive: false } required so preventDefault() suppresses page scroll.
        canvas.addEventListener('wheel', (e) => this._onCanvasWheel(e), { passive: false });

        // Splitter drag — mousedown on the handle starts a drag; subsequent
        // mousemove updates while the button is held are tracked on window
        // so the drag continues even if the cursor leaves the canvas.
        canvas.addEventListener('mousedown', (e) => {
            const y = this._canvasYFromEvent(e, canvas);
            if (this._isOnSplitter(y, canvas.height)) {
                this._splitterDragging = true;
                e.preventDefault();
                if (this._lastBins) this._render();
            }
        });

        window.addEventListener('mousemove', (e) => {
            if (!this._splitterDragging) return;
            const c = document.getElementById(this._canvasId);
            if (!c) return;
            const y = this._canvasYFromEvent(e, c);
            const ratio = Math.max(0.15, Math.min(0.85, y / c.height));
            this._splitRatio = ratio;
            if (this._lastBins) this._render();
        });

        window.addEventListener('mouseup', () => {
            if (!this._splitterDragging) return;
            this._splitterDragging = false;
            this._saveSplitRatio();
            if (this._lastBins) this._render();
        });

        // Crosshair tracking + splitter-hover cursor change.
        canvas.addEventListener('mousemove', (e) => {
            const rect = canvas.getBoundingClientRect();
            this._crosshairX = (e.clientX - rect.left) * (canvas.width  / rect.width);
            this._crosshairY = (e.clientY - rect.top)  * (canvas.height / rect.height);

            // Swap cursor to row-resize over the splitter and to a pointer over
            // the clickable scope-mode badge, so each affordance is obvious
            // before the user tries to interact.
            canvas.style.cursor = this._isOnSplitter(this._crosshairY, canvas.height)
                ? 'row-resize'
                : (this._isOnScopeModeBadge(this._crosshairX, this._crosshairY) ? 'pointer' : 'crosshair');

            if (this._lastBins) this._render();
            // Announce cursor frequency to screen readers via a live region (debounced to 1 s).
            if (this._lastSpanHz > 0 && this._viewCentreHz() > 0) {
                clearTimeout(this._announceTimer);
                this._announceTimer = setTimeout(() => {
                    const canvas2 = document.getElementById(this._canvasId);
                    if (!canvas2) return;
                    const W = canvas2.width;
                    const leftHz = this._viewLeftHz();
                    const cx = this._crosshairX;
                    if (cx == null) return;
                    const freqHz = leftHz + (cx / W) * this._lastSpanHz;
                    const freqLabel = (freqHz / 1e6).toFixed(6) + ' MHz';
                    this._announceToScreenReader('Spectrum cursor at ' + freqLabel);
                }, 1000);
            }
        });
        canvas.addEventListener('mouseleave', () => {
            this._crosshairX = null;
            this._crosshairY = null;
            clearTimeout(this._announceTimer);
            if (this._lastBins) this._render();
        });

        canvas.style.cursor = 'crosshair';
    }

    // Convert a mouse event's clientY to internal-canvas pixel coordinates.
    _canvasYFromEvent(e, canvas) {
        const rect = canvas.getBoundingClientRect();
        return (e.clientY - rect.top) * (canvas.height / rect.height);
    }

    // Hit test for the splitter — ±6 internal pixels of the boundary.
    _isOnSplitter(canvasY, canvasH) {
        const splitY = Math.floor(canvasH * this._splitRatio);
        return Math.abs(canvasY - splitY) <= 6;
    }

    // Hit test for the scope-mode badge (canvas-internal pixels). Null-safe:
    // the badge only exists while a CI-V scope mode is known.
    _isOnScopeModeBadge(canvasX, canvasY) {
        const b = this._scopeModeBadgeRect;
        if (!b || canvasX == null || canvasY == null) return false;
        return canvasX >= b.x && canvasX <= b.x + b.w &&
               canvasY >= b.y && canvasY <= b.y + b.h;
    }

    _onCanvasClick(e) {
        if (!this._lastBins || this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;

        const canvas = document.getElementById(this._canvasId);
        // Clicks on the splitter handle are for resizing, not tuning.
        if (this._isOnSplitter(this._canvasYFromEvent(e, canvas), canvas.height)) return;

        const rect   = canvas.getBoundingClientRect();
        const x      = e.clientX - rect.left;
        const W      = canvas.width;

        // Clicks on the scope-mode badge toggle the radio between Center and
        // Fixed rather than tuning. Clicking a green CENT badge flips to Fixed,
        // an amber FIX badge back to Center. The badge only appears once a sweep
        // has reported a mode.
        if (this._isOnScopeModeBadge(x * (W / rect.width), this._canvasYFromEvent(e, canvas))) {
            this.toggleScopeMode();
            return;
        }

        // Click-to-tune is active across the whole panel — both the live
        // spectrum (top ~45%) and the waterfall (bottom ~55%). Clicking a
        // signal trail in the waterfall QSYs to that column's frequency.

        // Convert canvas-relative x (CSS pixels) to canvas-internal pixels.
        const canvasX = x * (W / rect.width);
        const leftHz  = this._viewLeftHz();
        const clickHz = Math.round(leftHz + (canvasX / W) * this._lastSpanHz);

        // Shift+click → drop / toggle a persistent cursor at the click freq
        // instead of tuning. The operator can mark a station to come back to
        // while tuning around with normal clicks.
        if (e.shiftKey) {
            // If shift-click lands within 8 internal-pixels of the existing
            // pinned cursor, treat that as "remove the cursor". Otherwise
            // (re-)pin at the click frequency.
            if (this._pinnedCursorHz != null) {
                const px = ((this._pinnedCursorHz - leftHz) / this._lastSpanHz) * W;
                if (Math.abs(px - canvasX) <= 8) {
                    this._pinnedCursorHz = null;
                    if (this._lastBins) this._render();
                    return;
                }
            }
            this._pinnedCursorHz = clickHz;
            if (this._lastBins) this._render();
            return;
        }

        // If the click is within 8 internal-pixels of a spot's marker, snap
        // to that spot's exact frequency. Otherwise tune to the click x.
        let targetHz = clickHz;
        for (const s of this._spots) {
            const sx = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            if (Math.abs(sx - canvasX) <= 8) {
                targetHz = s.frequencyHz;
                break;
            }
        }

        fetch(`/api/cat/frequency/${this._vfoLower}`, {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ frequencyHz: targetHz }),
        }).catch(() => { /* ignore network errors */ });

        // Follow the click with a best-guess mode change. Most operators expect
        // jumping from 14.074 (FT8) to 14.284 (SSB) to also flip the radio to
        // USB rather than leave it stuck in DATA-U. window.setMode is defined
        // by site.js and uses the same CAT path the mode buttons use.
        const targetMode = modeForHz(targetHz);
        if (targetMode && window.setMode) {
            try { window.setMode(this._vfo, targetMode); } catch { /* ignore */ }
        }

        // Snap the live crosshair to the canvas centre so it visually
        // "follows" the clicked frequency once the spectrum recentres on
        // the new VFO. Without this the user's mouse hasn't moved but the
        // frequency under it has shifted left/right, so the crosshair label
        // would briefly show the wrong frequency until the next mousemove.
        // The next real mousemove resets _crosshairX to wherever the mouse
        // actually is, so this is a one-shot visual fixup, not persistent.
        //
        // Only in Centre mode. A Fixed-mode window does not move when the VFO
        // does, so the frequency under the mouse is still the one that was
        // clicked and moving the crosshair would invent an error.
        if (this._viewFollowsVfo()) this._crosshairX = Math.floor(W / 2);
    }

    _onCanvasWheel(e) {
        e.preventDefault();   // stop the page from scrolling
        if (!this._lastBins || this._vfoHz <= 0) return;

        // 1 kHz per notch — accumulate on _wheelTargetHz so rapid scrolling
        // compounds correctly before the radio confirms the new frequency.
        const step = 1000;
        const direction = e.deltaY > 0 ? -1 : 1;   // scroll up = higher freq
        this._wheelTargetHz = Math.max(30_000, Math.min(75_000_000,
            (this._wheelTargetHz ?? this._vfoHz) + direction * step));

        // Debounce: send once scrolling pauses for 60 ms.
        clearTimeout(this._wheelTimer);
        this._wheelTimer = setTimeout(() => {
            const hz = this._wheelTargetHz;
            this._wheelTargetHz = null;
            fetch(`/api/cat/frequency/${this._vfoLower}`, {
                method:  'POST',
                headers: { 'Content-Type': 'application/json' },
                body:    JSON.stringify({ frequencyHz: hz }),
            }).catch(() => {});
        }, 60);
    }

    _sizeCanvas(canvas) {
        const w = canvas.parentElement
            ? canvas.parentElement.clientWidth || 800
            : 800;
        canvas.width  = w;
        canvas.height = 280;   // 126px spectrum + 154px waterfall
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    // @param {boolean} scrollWaterfall  Whether to advance the waterfall by
    //   one row this frame. Defaults to FALSE so the many cosmetic forced
    //   redraws (crosshair on mousemove, cursor, hold, resize, dB/Bright
    //   changes) repaint the spectrum WITHOUT scrolling the waterfall — moving
    //   the mouse must not make the waterfall march. Only update(), fed by a
    //   real FFT frame, passes true (throttled by _waterfallSpeed). The
    //   !_waterfallData guard below still lets the very first paint build the
    //   buffer regardless.
    _render(scrollWaterfall = false) {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas || !this._lastBins) return;

        // Out of range (Phase 5 pseudo-dual: the watch VFO has moved outside the
        // shared sweep) — the last frame is stale and meaningless, so keep the
        // "off-screen" overlay instead of repainting it. Without this, any
        // mouse-driven re-render (crosshair tracking on hover) would paint the
        // stale trace over the overlay and the message would vanish.
        if (this._status === 'outofrange') {
            this._drawStatusOverlay('outofrange');
            return;
        }

        const ctx         = canvas.getContext('2d');
        const W           = canvas.width;
        const H           = canvas.height;
        const specH       = Math.max(1, Math.floor(H * this._splitRatio));
        const wfH         = Math.max(1, H - specH);
        const bins        = this._lastBins;
        const centreHz    = this._lastCentreHz;
        const spanHz      = this._lastSpanHz;

        this._drawSpectrum(ctx, bins, W, specH);
        this._drawOutOfBandShade(ctx, W, specH);
        this._drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz);
        this._drawBandEdges(ctx, W, specH);
        this._drawBandMarkers(ctx, W, specH);
        this._drawSpots(ctx, W, specH);
        this._drawDxBadge(ctx, W);
        this._drawScopeModeBadge(ctx);
        if (scrollWaterfall || !this._waterfallData) this._scrollWaterfall(ctx, bins, W, specH, wfH);
        this._drawPinnedCursor(ctx, W, specH);
        this._drawCrosshair(ctx, W, specH, spanHz);
        this._drawHoldOverlay(ctx, W, specH);
        this._drawSplitterHandle(ctx, W, specH);
    }

    // Horizontal handle the user can grab to resize the spectrum vs waterfall.
    // Drawn on top of everything so it stays visible against the waterfall.
    _drawSplitterHandle(ctx, W, specH) {
        ctx.save();
        ctx.fillStyle = this._splitterDragging ? 'rgba(0, 200, 255, 0.85)'
                                                : 'rgba(120, 140, 160, 0.55)';
        ctx.fillRect(0, specH - 1, W, 2);
        // Centre grip indicator — two short bars so the affordance is obvious.
        const gripW = 28, gripH = 2, cx = Math.floor(W / 2);
        ctx.fillStyle = this._splitterDragging ? 'rgba(0, 200, 255, 1)'
                                                : 'rgba(200, 210, 220, 0.85)';
        ctx.fillRect(cx - gripW / 2,     specH - 3, gripW, gripH);
        ctx.fillRect(cx - gripW / 2,     specH + 1, gripW, gripH);
        ctx.restore();
    }

    // ── Persistent (pinned) cursor ───────────────────────────────────────────
    //
    // A "bookmark" cursor the operator dropped with Shift+click. Distinct
    // visual from the live mouse crosshair: solid cyan vertical line plus a
    // boxed frequency label so it stands out. Shift+click on or very near
    // the existing cursor clears it.
    _drawPinnedCursor(ctx, W, specH) {
        if (this._pinnedCursorHz == null || this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;
        const leftHz = this._viewLeftHz();
        const rightHz = leftHz + this._lastSpanHz;
        if (this._pinnedCursorHz < leftHz || this._pinnedCursorHz > rightHz) return;

        const x = ((this._pinnedCursorHz - leftHz) / this._lastSpanHz) * W;
        const axisH = 20;
        const specTop = specH - axisH;

        ctx.save();

        // Solid cyan vertical line spanning the whole panel (spectrum + waterfall)
        // so the marker is visible even when the operator is studying the waterfall.
        ctx.strokeStyle = '#00d4ff';
        ctx.lineWidth = 1.5;
        ctx.beginPath();
        ctx.moveTo(x + 0.5, 0);
        ctx.lineTo(x + 0.5, specH);
        ctx.stroke();

        // Frequency label in a boxed background near the top of the spectrum.
        const label = (this._pinnedCursorHz / 1e6).toFixed(6) + ' MHz';
        ctx.font = 'bold 11px monospace';
        const padX = 4, padY = 2;
        const textWidth = ctx.measureText(label).width;
        const boxW = textWidth + padX * 2;
        const boxH = 16;
        // Prefer the label to the right of the cursor; flip to left near the right edge.
        const boxX = (x + boxW + 8 < W) ? x + 4 : x - boxW - 4;
        const boxY = 24;

        ctx.fillStyle = 'rgba(0, 60, 90, 0.9)';
        ctx.fillRect(boxX, boxY, boxW, boxH);
        ctx.strokeStyle = '#00d4ff';
        ctx.lineWidth = 1;
        ctx.strokeRect(boxX + 0.5, boxY + 0.5, boxW - 1, boxH - 1);

        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'left';
        ctx.textBaseline = 'top';
        ctx.fillText(label, boxX + padX, boxY + padY);

        ctx.restore();
    }

    // ── Hold overlay ─────────────────────────────────────────────────────────
    //
    // When setHold(true) freezes the spectrum, paint a subtle banner so the
    // operator sees the display isn't live. The status badge in the panel
    // header also says "Hold" (yellow), and the canvas is intentionally
    // not faded — operators want to study what's frozen, not look at
    // greyed-out data.
    _drawHoldOverlay(ctx, W, specH) {
        if (!this._hold) return;
        ctx.save();
        ctx.font         = 'bold 12px sans-serif';
        ctx.textAlign    = 'left';
        ctx.textBaseline = 'top';
        const label = 'HOLD';
        const padX = 6, padY = 3;
        const textWidth = ctx.measureText(label).width;
        ctx.fillStyle = 'rgba(200, 140, 0, 0.85)';
        ctx.fillRect(4, 4, textWidth + padX * 2, 20);
        ctx.fillStyle = '#000000';
        ctx.fillText(label, 4 + padX, 4 + padY);
        ctx.restore();
    }

    // ── Out-of-band dimming ──────────────────────────────────────────────────
    // The IC-7300's scope is a fixed ± window centred on the VFO, so on most
    // bands it spills past the amateur allocation (e.g. on 20 m the sweep runs
    // below 14.000 and above 14.350). Rather than crop — which would change the
    // horizontal scale per band and break every overlay's hz→x maths — we lay a
    // translucent dark veil over the out-of-band columns so the in-band section
    // clearly stands out while adjacent activity stays visible (just greyed).
    // Drawn between the trace and the axis/edge lines so those render crisp on
    // top. Same edge data as _drawBandEdges.
    _drawOutOfBandShade(ctx, W, specH) {
        if (this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;
        const leftHz  = this._viewLeftHz();
        const rightHz = leftHz + this._lastSpanHz;
        const edges = this._bandEdges ?? SpectrumPanel.BAND_EDGES;
        if (!edges || !edges.length) return;

        // In-band intervals overlapping the visible window, clipped to it.
        const inBand = [];
        for (const edge of edges) {
            const lo = Math.max(edge.lo, leftHz);
            const hi = Math.min(edge.hi, rightHz);
            if (hi > lo) inBand.push([lo, hi]);
        }
        inBand.sort((a, b) => a[0] - b[0]);

        const hzToX = (hz) => ((hz - leftHz) / this._lastSpanHz) * W;

        ctx.save();
        ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';   // dim veil over out-of-band

        // Shade every gap: window-start → first band, between bands, last band →
        // window-end. If no band overlaps the window the whole thing is shaded.
        let cursor = leftHz;
        for (const [lo, hi] of inBand) {
            if (lo > cursor) {
                const x0 = hzToX(cursor);
                ctx.fillRect(x0, 0, hzToX(lo) - x0, specH);
            }
            if (hi > cursor) cursor = hi;
        }
        if (cursor < rightHz) {
            const x0 = hzToX(cursor);
            ctx.fillRect(x0, 0, hzToX(rightHz) - x0, specH);
        }
        ctx.restore();
    }

    // ── Band-edge guard rails ────────────────────────────────────────────────
    // Vertical red lines at the lower and upper edges of each amateur band
    // visible in the current spectrum window. Makes it visually obvious if
    // the operator tunes (deliberately or accidentally) outside the amateur
    // allocation. Edges are the broadest amateur envelopes (worldwide), so a
    // line at e.g. 7.300 MHz on 40m might be lenient in a Region 1 country
    // where the band ends at 7.200 — but it's never wrong (no transmission
    // is legal beyond these limits in any region).
    _drawBandEdges(ctx, W, specH) {
        if (this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;
        const leftHz  = this._viewLeftHz();
        const rightHz = leftHz + this._lastSpanHz;

        // Per-region edges (set by Index.cshtml from BAND_EDGES[region]) take
        // priority over the class-static worldwide envelope. Fall back if no
        // region-specific data has been supplied.
        const edges = this._bandEdges ?? SpectrumPanel.BAND_EDGES;

        ctx.save();
        ctx.strokeStyle = '#ff4040';
        ctx.lineWidth   = 1.5;
        ctx.setLineDash([4, 3]);   // dashed so it's clearly a "guard" not a "marker"

        for (const edge of edges) {
            for (const hz of [edge.lo, edge.hi]) {
                if (hz < leftHz || hz > rightHz) continue;
                const x = ((hz - leftHz) / this._lastSpanHz) * W;
                ctx.beginPath();
                ctx.moveTo(x, 0);
                ctx.lineTo(x, specH - 2);
                ctx.stroke();
            }
        }
        ctx.restore();
    }

    // ── Band-plan marker ticks ───────────────────────────────────────────────
    // Vertical ticks at the standard CW / FT8 / FT4 / RTTY / SSB activity
    // frequencies for the current region's band plan. Helps orient newer
    // operators who don't yet have the watering holes memorised.
    //
    // Labels are staggered across up to three rows when they would overlap
    // (e.g. FT8 at 14.074 and RTTY at 14.080 are only 6 kHz apart and would
    // collide at any reasonable span).
    _drawBandMarkers(ctx, W, specH) {
        if (!this._bandPlan || this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;

        const leftHz  = this._viewLeftHz();
        const rightHz = leftHz + this._lastSpanHz;

        // Collect all in-window markers with their pixel x and label width.
        const markers = [];
        for (const bandName in this._bandPlan) {
            const segments = this._bandPlan[bandName];
            for (const segKey in segments) {
                const seg = segments[segKey];
                if (!seg || typeof seg.freq !== 'number') continue;
                if (seg.freq < leftHz || seg.freq > rightHz) continue;
                const x     = ((seg.freq - leftHz) / this._lastSpanHz) * W;
                const label = seg.label || segKey;
                markers.push({ x, label });
            }
        }
        if (markers.length === 0) return;
        markers.sort((a, b) => a.x - b.x);

        ctx.save();
        // Band-plan segment marker font: bumped from 11px → 13px for
        // accessibility (Colin 2026-06-13 — the original size required a
        // magnifying glass to read the FT8/CW/SSB labels above the
        // spectrum).
        ctx.font        = '13px sans-serif';
        ctx.textAlign   = 'center';
        ctx.lineWidth   = 1;
        ctx.strokeStyle = '#80c0ff';
        ctx.fillStyle   = '#80c0ff';

        // Stagger labels across up to three rows so closely-spaced markers
        // (FT8 + RTTY on 20m being the worst case) remain readable.
        const rowGap       = 12;   // vertical spacing between label rows
        const maxRows      = 3;
        const minLabelGap  = 4;
        const rowRightEdge = new Array(maxRows).fill(-Infinity);

        for (const m of markers) {
            const halfWidth = ctx.measureText(m.label).width / 2;
            const leftEdge  = m.x - halfWidth;
            const rightEdge = m.x + halfWidth;

            let row = 0;
            for (let r = 0; r < maxRows; r++) {
                if (leftEdge >= rowRightEdge[r] + minLabelGap) { row = r; break; }
                row = r;
            }
            rowRightEdge[row] = rightEdge;

            // Tick mark — same position regardless of which row the label is on.
            ctx.beginPath();
            ctx.moveTo(m.x, specH - 18);
            ctx.lineTo(m.x, specH - 4);
            ctx.stroke();

            // Label — rendered above the tick, pushed up by `row` × rowGap
            // so overlapping labels stack vertically instead of overwriting.
            const labelY = specH - 20 - row * rowGap;
            ctx.fillText(m.label, m.x, labelY);
        }
        ctx.restore();
    }

    // ── DX cluster status badge ──────────────────────────────────────────────
    _drawDxBadge(ctx, W) {
        const label = `DX: ${this._dxStatus}`;
        let bg, fg;
        switch (this._dxStatus) {
            case 'connected':    bg = '#1e7e34'; fg = '#ffffff'; break;
            case 'connecting':   bg = '#b58900'; fg = '#000000'; break;
            case 'disconnected': bg = '#a03030'; fg = '#ffffff'; break;
            default:             bg = '#3a3a3a'; fg = '#aaaaaa'; break;
        }

        ctx.save();
        ctx.font         = 'bold 14px sans-serif';
        ctx.textBaseline = 'top';
        const padX = 8;
        const padY = 5;
        const textWidth = ctx.measureText(label).width;
        const w = textWidth + padX * 2;
        const h = 24;
        const x = W - w - 4;
        const y = 4;
        ctx.fillStyle = bg;
        ctx.fillRect(x, y, w, h);
        ctx.fillStyle = fg;
        ctx.textAlign = 'left';
        ctx.fillText(label, x + padX, y + padY);
        ctx.restore();
    }

    // Small badge in the top-left showing the radio's live scope mode
    // (CENT / FIX / SCROLL-C / SCROLL-F), read from the 27 00 waveform header.
    // _scopeMode stays undefined until a sweep carries one, so the badge is
    // drawn only when a mode is actually known.
    //
    // The axis no longer assumes Centre — it is drawn from the window centre the
    // sweep header reports (see _viewCentreHz) — so amber is no longer a
    // "the scale may be wrong" caution. Fixed mode was checked against an
    // IC-7300 MkII on 2026-08-13: on 20 m the radio reported 14.000–14.350 and
    // the trace, axis, band-plan markers and DX spots all lined up. Amber now
    // just distinguishes the mode at a glance; the two scroll modes share it
    // because they decode through the same lower/upper-edge path.
    _drawScopeModeBadge(ctx) {
        const mode = this._scopeMode;
        if (!mode) { this._scopeModeBadgeRect = null; return; }

        const aligned = (mode === 'CENT');
        const bg = aligned ? '#1e7e34' : '#b58900';
        const fg = aligned ? '#ffffff' : '#000000';

        ctx.save();
        ctx.font         = 'bold 14px sans-serif';
        ctx.textBaseline = 'top';
        const padX = 8;
        const padY = 5;
        const w = ctx.measureText(mode).width + padX * 2;
        const h = 24;
        const x = 4;
        const y = 4;
        ctx.fillStyle = bg;
        ctx.fillRect(x, y, w, h);
        ctx.fillStyle = fg;
        ctx.textAlign = 'left';
        ctx.fillText(mode, x + padX, y + padY);
        ctx.restore();

        // Remember the hit rect (canvas-internal pixels) so a click here toggles
        // the radio's scope mode instead of tuning.
        this._scopeModeBadgeRect = { x, y, w, h };
    }

    // ── DX cluster spot overlay ──────────────────────────────────────────────
    //
    // Draws a small downward-pointing tick at each spot's frequency with the
    // callsign label above. Spots outside the current span are skipped. When
    // multiple spots fall within ~50 px of each other their labels are
    // staggered vertically so they don't overlap.
    _drawSpots(ctx, W, specH) {
        if (!this._spots.length || this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;

        const leftHz  = this._viewLeftHz();
        const rightHz = leftHz + this._lastSpanHz;
        // When the user has ticked "Show only watched callsigns" in the DX
        // Watch popup, hide every spot that isn't flagged isWatched. The flag
        // is set by DxClusterService on the backend, so we just respect it.
        const onlyWatched = !!window.dxOnlyWatched;

        // Build a list of (x, spot) for spots in range, sorted left-to-right.
        const drawList = [];
        for (const s of this._spots) {
            if (s.frequencyHz < leftHz || s.frequencyHz > rightHz) continue;
            if (onlyWatched && !s.isWatched) continue;
            const x = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            drawList.push({ x, spot: s });
        }
        if (drawList.length === 0) return;
        drawList.sort((a, b) => a.x - b.x);

        ctx.save();
        ctx.font      = 'bold 13px sans-serif';
        ctx.textAlign = 'center';
        ctx.lineWidth   = 1.5;

        // Stagger labels across multiple rows so they don't overlap. For each
        // candidate label, measure its actual rendered width and find the
        // first row whose previous label's right edge is far enough left.
        // If no row has space, drop the label rather than overlap. Newer
        // spots are drawn last so they win when crowded — drawList is sorted
        // by x but spots are most-recent-first within the same x.
        //
        // Watched callsigns are drawn in bright red instead of yellow so the
        // operator can spot them at a glance amongst the regular cluster
        // traffic.
        const rowHeight    = 16;
        const maxRows      = 5;
        const minLabelGap  = 4;   // px between adjacent labels in the same row
        const rowRightEdge = new Array(maxRows).fill(-Infinity);

        for (const { x, spot } of drawList) {
            const halfWidth = ctx.measureText(spot.callsign).width / 2;
            const leftEdge  = x - halfWidth;
            const rightEdge = x + halfWidth;

            let row = -1;
            for (let r = 0; r < maxRows; r++) {
                if (leftEdge >= rowRightEdge[r] + minLabelGap) { row = r; break; }
            }
            if (row < 0) continue; // crowded — skip this label rather than overlap
            rowRightEdge[row] = rightEdge;
            const labelY = 14 + row * rowHeight;

            // Colour per spot — bright red for watched callsigns, yellow otherwise.
            const colour = spot.isWatched ? '#ff4040' : '#ffcc33';
            ctx.fillStyle   = colour;
            ctx.strokeStyle = colour;

            // Callsign label.
            ctx.fillText(spot.callsign, x, labelY);

            // Tick mark dropping from the label to mid-spectrum.
            const tickTop = labelY + 2;
            const tickBot = tickTop + 6;
            ctx.beginPath();
            ctx.moveTo(x, tickTop);
            ctx.lineTo(x, tickBot);
            ctx.stroke();
        }

        ctx.restore();
    }

    // ── Spectrum trace ───────────────────────────────────────────────────────

    _drawSpectrum(ctx, bins, W, specH) {
        const N      = bins.length;
        const dbMin  = this._dbMin;
        const dbMax  = this._dbMax;
        const range  = dbMax - dbMin;

        // The bottom AXIS_H px of the spectrum zone are reserved for the frequency
        // labels (drawn later by _drawFrequencyAxis). Map the trace, grid and dB
        // scale into the area *above* that strip so the noise floor can sit right on
        // top of the labels instead of hiding behind them — that's what removes the
        // dead space the operator saw between the floor and the waterfall.
        const H = Math.max(1, specH - SpectrumPanel.AXIS_H);

        // Background — fill the whole zone (incl. the axis strip's backing) so no
        // gap shows; the axis strip repaints its own darker band over the bottom.
        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, specH);

        // Horizontal grid lines at a "nice" dB step chosen for the current range,
        // aligned to round multiples so the scale reads cleanly at any Range.
        const dbStep = this._niceDbStep(range);
        ctx.strokeStyle = '#1e2030';
        ctx.lineWidth   = 1;
        for (let db = Math.ceil(dbMin / dbStep) * dbStep; db <= dbMax; db += dbStep) {
            const y = H - ((db - dbMin) / range) * H;
            ctx.beginPath();
            ctx.moveTo(0, y);
            ctx.lineTo(W, y);
            ctx.stroke();
        }

        // Build the trace path
        ctx.beginPath();
        for (let i = 0; i < N; i++) {
            const x = (i / N) * W;
            const y = H - Math.max(0, Math.min(1, (bins[i] - dbMin) / range)) * H;
            if (i === 0) ctx.moveTo(x, y);
            else         ctx.lineTo(x, y);
        }

        // Close path to the bottom of the trace area (top of the axis strip) to fill
        ctx.lineTo(W, H);
        ctx.lineTo(0, H);
        ctx.closePath();

        ctx.fillStyle = 'rgba(0, 140, 255, 0.18)';
        ctx.fill();

        // Redraw the outline on top of the fill
        ctx.beginPath();
        for (let i = 0; i < N; i++) {
            const x = (i / N) * W;
            const y = H - Math.max(0, Math.min(1, (bins[i] - dbMin) / range)) * H;
            if (i === 0) ctx.moveTo(x, y);
            else         ctx.lineTo(x, y);
        }
        ctx.strokeStyle = '#00aaff';
        ctx.lineWidth   = 1.5;
        ctx.stroke();

        // dB scale down the right edge — shows the exact values the Range window
        // spans, including the true top and bottom, so the vertical scale is
        // readable at a glance. Confined to the trace area (above the axis strip).
        this._drawDbScale(ctx, W, H, dbMin, dbMax, range, dbStep);

        // VFO frequency readout. Centred text, not a position marker — in Fixed
        // mode the VFO is wherever the marker line on the axis says it is.
        if (this._vfoHz > 0) {
            const label = (this._vfoHz / 1e6).toFixed(6) + ' MHz';
            ctx.font      = '12px monospace';
            ctx.fillStyle = '#44aaff';
            ctx.textAlign = 'center';
            ctx.fillText(label, W / 2, 14);
        }
    }

    // Pick a "nice" dB grid step (5/10/20/25/50/100) that yields roughly half a
    // dozen gridlines across the current vertical range, so the scale stays
    // legible whether the Range slider is at 20 dB or 140 dB.
    _niceDbStep(range) {
        const steps = [5, 10, 20, 25, 50, 100];
        const target = 6;
        return steps.find(s => range / s <= target) ?? steps[steps.length - 1];
    }

    // Draw the dB scale down the right edge: a label at each nice gridline level
    // plus the exact top and bottom of the window, each on a dark backing strip so
    // it stays readable over the trace. This is the "what dB is it set to" readout.
    _drawDbScale(ctx, W, H, dbMin, dbMax, range, step) {
        if (range <= 0) return;

        // Nice multiples inside the window, plus the true endpoints.
        const levels = new Set();
        for (let db = Math.ceil(dbMin / step) * step; db <= dbMax; db += step)
            levels.add(db);
        levels.add(dbMin);
        levels.add(dbMax);

        ctx.save();
        ctx.font         = '12px monospace';
        ctx.textAlign    = 'right';
        ctx.textBaseline = 'alphabetic';
        for (const db of levels) {
            // Clamp so the top/bottom labels sit fully on-screen rather than being
            // clipped at the very edge.
            let y = H - ((db - dbMin) / range) * H;
            y = Math.max(12, Math.min(H - 3, y));

            const text = db === dbMax ? `${db} dB` : `${db}`;
            const tw   = ctx.measureText(text).width;

            ctx.fillStyle = 'rgba(6, 8, 16, 0.72)';
            ctx.fillRect(W - tw - 10, y - 12, tw + 10, 14);

            ctx.strokeStyle = 'rgba(120, 140, 170, 0.55)';
            ctx.lineWidth   = 1;
            ctx.beginPath();
            ctx.moveTo(W - tw - 12, y - 5);
            ctx.lineTo(W - tw - 7,  y - 5);
            ctx.stroke();

            ctx.fillStyle = '#9fb3cc';
            ctx.fillText(text, W - 4, y - 2);
        }
        ctx.restore();
    }

    // ── Frequency axis ───────────────────────────────────────────────────────

    _drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz) {
        const axisH  = SpectrumPanel.AXIS_H;
        const tickY0 = specH - axisH;       // top of axis strip
        const labelY = specH - 4;           // baseline for text

        ctx.fillStyle = '#111118';
        ctx.fillRect(0, tickY0, W, axisH);

        // Only skip labels when FrequencyA has never been set (C# long default = 0).
        // Any non-zero persisted frequency is treated as valid; FTdx101MP range is 30 kHz–75 MHz.
        if (this._viewCentreHz() <= 0) {
            ctx.fillStyle = '#667799';
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText('No VFO frequency available', W / 2, labelY);
            return;
        }

        // First tick at the next multiple of stepHz above the left edge. The
        // window's own centre, not the VFO — see _viewCentreHz.
        const leftHz = this._viewLeftHz(spanHz);

        // VFO marker line (drawn first, behind labels), at the VFO's real
        // position in the window. In Centre mode that is the middle of the
        // canvas; in Fixed mode it travels as the operator tunes, which is the
        // whole point of Fixed. Skipped when the VFO is outside the window.
        if (this._vfoHz > 0 && spanHz > 0) {
            const vfoX = ((this._vfoHz - leftHz) / spanHz) * W;
            if (vfoX >= 0 && vfoX <= W) {
                ctx.strokeStyle = 'rgba(0, 170, 255, 0.4)';
                ctx.lineWidth   = 1;
                ctx.beginPath();
                ctx.moveTo(vfoX, 0);
                ctx.lineTo(vfoX, tickY0);
                ctx.stroke();
            }
        }

        // Choose a "nice" tick interval that gives roughly 6–12 ticks across the span.
        // Candidate steps in Hz: 50k, 100k, 200k, 250k, 500k, 1M, 2M, 5M, 10M
        const steps = [50e3, 100e3, 200e3, 250e3, 500e3, 1e6, 2e6, 5e6, 10e6];
        const targetTicks = 8;
        const stepHz = steps.find(s => spanHz / s <= targetTicks) ?? steps[steps.length - 1];

        const firstHz = Math.ceil(leftHz / stepHz) * stepHz;

        // Frequency-axis tick label font: bumped from 10px → 13px for
        // accessibility (Colin 2026-06-13 — the MHz numbers under the
        // tick marks were unreadable without a magnifying glass).
        // 13px fits within the existing 20px axisH (label baseline at
        // specH-4 leaves room for glyphs above).
        ctx.font      = '13px monospace';
        ctx.textAlign = 'center';

        for (let tickHz = firstHz; tickHz <= leftHz + spanHz; tickHz += stepHz) {
            const x = ((tickHz - leftHz) / spanHz) * W;

            // Tick line
            const isVfo = Math.abs(tickHz - this._vfoHz) < stepHz * 0.01;
            ctx.strokeStyle = isVfo ? 'rgba(0,170,255,0.8)' : '#334466';
            ctx.lineWidth   = 1;
            ctx.beginPath();
            ctx.moveTo(x, tickY0);
            ctx.lineTo(x, tickY0 + 4);
            ctx.stroke();

            // Label — skip if too close to edge or below 0 Hz
            if (x < 24 || x > W - 24 || tickHz <= 0) continue;

            const mhz    = tickHz / 1e6;
            const label  = mhz.toFixed(6);

            ctx.fillStyle = isVfo ? '#44aaff' : '#8899bb';
            ctx.fillText(label, x, labelY);
        }
    }

    // ── Crosshair overlay ────────────────────────────────────────────────────

    _drawCrosshair(ctx, W, specH, spanHz) {
        if (this._crosshairX === null || this._lastSpanHz <= 0 || this._viewCentreHz() <= 0) return;

        const x = this._crosshairX;
        const y = this._crosshairY;

        // Only draw inside the spectrum area (above the axis strip).
        const axisH = 20;
        const specTop = specH - axisH;
        if (y < 0 || y > specTop) return;

        // Vertical line
        ctx.save();
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.5)';
        ctx.lineWidth   = 1;
        ctx.setLineDash([4, 4]);
        ctx.beginPath();
        ctx.moveTo(x, 0);
        ctx.lineTo(x, specTop);
        ctx.stroke();

        // Horizontal line
        ctx.beginPath();
        ctx.moveTo(0, y);
        ctx.lineTo(W, y);
        ctx.stroke();
        ctx.setLineDash([]);

        // Frequency at cursor
        const leftHz  = this._viewLeftHz(spanHz);
        const freqHz  = leftHz + (x / W) * spanHz;
        const label   = (freqHz / 1e6).toFixed(6) + ' MHz';

        // Crosshair frequency readout font: bumped from 11px → 14px for
        // accessibility (Colin 2026-06-13, same release as the axis-label
        // bumps). Background box height grew from 16px → 20px and the
        // y-offset from -12 to -15 to fit the larger glyphs cleanly.
        ctx.font      = '14px monospace';
        const pad     = 4;
        const tw      = ctx.measureText(label).width;

        // Position label to the right of cursor, flip left near the right edge.
        const lx = (x + tw + pad * 2 + 6 < W) ? x + pad : x - tw - pad * 2;
        const ly = Math.max(17, Math.min(y - pad, specTop - 4));

        ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
        ctx.fillRect(lx, ly - 15, tw + pad * 2, 20);

        ctx.fillStyle = '#ffffff';
        ctx.textAlign = 'left';
        ctx.fillText(label, lx + pad, ly);

        ctx.restore();
    }

    // ── Waterfall ────────────────────────────────────────────────────────────

    _scrollWaterfall(ctx, bins, W, specH, wfH) {
        // Lazily allocate or reallocate when the canvas size changes.
        if (!this._waterfallData || this._waterfallCols !== W || this._waterfallRows !== wfH) {
            this._waterfallCols = W;
            this._waterfallRows = wfH;
            this._waterfallData = ctx.createImageData(W, wfH);
            // Initialise to black.
            this._waterfallData.data.fill(0);
            for (let p = 3; p < this._waterfallData.data.length; p += 4)
                this._waterfallData.data[p] = 255;   // alpha = 255
        }

        const data = this._waterfallData.data;
        const N    = bins.length;

        // Shift all existing rows down by one pixel (4 bytes per pixel, W pixels per row).
        const rowBytes = W * 4;
        data.copyWithin(rowBytes, 0, data.length - rowBytes);

        // Draw new row at the top.
        for (let x = 0; x < W; x++) {
            const binIdx = Math.floor((x / W) * N);
            const [r, g, b] = this._dbToColor(bins[binIdx]);
            const p = x * 4;
            data[p + 0] = r;
            data[p + 1] = g;
            data[p + 2] = b;
            data[p + 3] = 255;
        }

        ctx.putImageData(this._waterfallData, 0, specH);
    }

    // ── Status overlay ───────────────────────────────────────────────────────

    _drawStatusOverlay(status) {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas) return;

        const ctx = canvas.getContext('2d');
        const W   = canvas.width;
        const H   = canvas.height;

        if (status === 'streaming') {
            // Clear any previous overlay; the next data frame will paint correctly.
            return;
        }

        // If we have a previous frame and this is just a brief transition
        // (span change, radio reconnect), keep the existing spectrum visible
        // instead of wiping to a "connecting…" message. The status badge in
        // the panel header carries the state info; blanking out a working
        // spectrum for a 3-second reconnect is jarring.
        if ((status === 'connecting' || status === 'disconnected') && this._lastBins) {
            this._render();
            return;
        }

        const messages = {
            connecting:   'Waiting for the radio’s band scope…',
            disconnected: 'Radio unavailable — retrying every 5 s',
            unconfigured: 'Band scope is off — switch it on above the panel',
            outofrange:   'Watch VFO is off-screen — widen the scope span, or turn on cross-band peek in Settings',
            blocked:      'The radio refused to send scope data',
        };

        const line1 = messages[status] ?? `Scope status: ${status}`;
        const detail = (status === 'disconnected' || status === 'blocked') && this._errorDetail
            ? this._errorDetail
            : null;

        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, H);

        ctx.textAlign = 'center';

        // The "blocked" reason is a couple of sentences of instructions (which
        // radio menu to change, and to what), so it has to wrap. The other
        // details are short enough that one line has always been fine, and
        // wrapping them costs nothing.
        ctx.font = '11px sans-serif';
        const lines = detail ? this._wrapText(ctx, detail, W - 32) : [];

        const lineH = 15;
        // Centre the whole block — heading plus however many wrapped lines.
        const top = H / 2 - (lines.length * lineH) / 2;

        ctx.fillStyle = '#8899bb';
        ctx.font = '14px sans-serif';
        ctx.fillText(line1, W / 2, top);

        ctx.font      = '11px sans-serif';
        ctx.fillStyle = '#cc6655';
        lines.forEach((l, i) => ctx.fillText(l, W / 2, top + 18 + i * lineH));
    }

    /** Greedy word-wrap for overlay text. Returns an array of lines. */
    _wrapText(ctx, text, maxWidth) {
        const words = String(text).split(/\s+/).filter(Boolean);
        const lines = [];
        let line = '';
        for (const w of words) {
            const next = line ? line + ' ' + w : w;
            if (line && ctx.measureText(next).width > maxWidth) {
                lines.push(line);
                line = w;
            } else {
                line = next;
            }
        }
        if (line) lines.push(line);
        return lines;
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    /** Announce text to screen readers via the shared ARIA live region in site.js. */
    _announceToScreenReader(text) {
        let lr = document.getElementById('_sr_live');
        if (!lr) {
            // Fallback: create a local live region if site.js hasn't run yet.
            // assertive (matching site.js's primary live region) so new
            // cursor announcements interrupt rather than queue.
            lr = document.createElement('div');
            lr.id = '_sr_live';
            lr.setAttribute('aria-live', 'assertive');
            lr.setAttribute('aria-atomic', 'true');
            lr.style.cssText = 'position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;';
            document.body.appendChild(lr);
        }
        lr.textContent = '';
        requestAnimationFrame(() => { lr.textContent = text; });
    }

    // ── Color mapping ────────────────────────────────────────────────────────

    /**
     * Maps a dBFS value to an RGB thermal colour: black → blue → cyan →
     * green → yellow → red.
     *
     * The waterfall uses a FIXED reference window (−120 … 0 dBFS), deliberately
     * independent of the Low/High trace sliders, so "Bright = Off" always means
     * the same dark baseline. The Bright slider (_wfBrightDb) is the only thing
     * that lifts weak signals up the colour scale — it's YWC's old "gain"
     * brightening, as a dedicated waterfall control. Instance method (not
     * static) so it can read the per-panel brightness.
     */
    _dbToColor(db) {
        // Bright slider adds _wfBrightDb of lift before mapping into the fixed
        // 120 dB window; at Off (0) the mapping is the original dark baseline.
        const t = Math.max(0, Math.min(1, (db + this._wfBrightDb + 120) / 120));
        if (t < 0.2)  return [0,                   0,                   Math.round(t * 5 * 180)];
        if (t < 0.4)  return [0,                   Math.round((t - 0.2) * 5 * 200), 180];
        if (t < 0.6)  return [0,                   200,                 Math.round(180 - (t - 0.4) * 5 * 180)];
        if (t < 0.8)  return [Math.round((t - 0.6) * 5 * 255), 200,    0];
        return               [255,                 Math.round(200 - (t - 0.8) * 5 * 200), 0];
    }
}

// Amateur band envelopes in Hz — the broadest worldwide limits, used to draw
// red guard-rail lines at the edges of each band on the spectrum. These are
// not per-region; a Region-1 operator may see a guard rail at 7.300 MHz where
// the legal limit is actually 7.200, but the inverse is never true (no region
// permits TX beyond these envelopes).
SpectrumPanel.BAND_EDGES = [
    { name: '160m', lo:   1800000, hi:   2000000 },
    { name:  '80m', lo:   3500000, hi:   4000000 },
    { name:  '60m', lo:   5250000, hi:   5450000 },
    { name:  '40m', lo:   7000000, hi:   7300000 },
    { name:  '30m', lo:  10100000, hi:  10150000 },
    { name:  '20m', lo:  14000000, hi:  14350000 },
    { name:  '17m', lo:  18068000, hi:  18168000 },
    { name:  '15m', lo:  21000000, hi:  21450000 },
    { name:  '12m', lo:  24890000, hi:  24990000 },
    { name:  '10m', lo:  28000000, hi:  29700000 },
    { name:   '6m', lo:  50000000, hi:  54000000 },
    { name:   '4m', lo:  70000000, hi:  70500000 },
    { name:   '2m', lo: 144000000, hi: 148000000 },
];
