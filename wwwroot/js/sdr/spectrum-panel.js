// FTdx101 WebApp – Spectrum Panel
// UI module — DOM access is intentional and correct here.
// Owns a single <canvas> element that is divided into two rendering zones:
//   Top 45%  — spectrum trace (line graph of dBFS vs frequency)
//   Bottom 55% — waterfall  (scrolling time–frequency heatmap)
//
// Frequency axis labels are computed from the VFO frequency reported by
// SdrSpectrumPipeline so the display is always centred on the current band.

export class SpectrumPanel {

    /**
     * @param {string} canvasId       ID of the <canvas> element to render into.
     * @param {string} containerId    ID of the wrapper element to show/hide.
     * @param {number} initialVfoHz   Starting VFO A frequency in Hz.
     */
    constructor(canvasId, containerId, initialVfoHz = 14_074_000) {
        this._canvasId    = canvasId;
        this._containerId = containerId;
        this._vfoHz       = initialVfoHz;
        this._status      = 'unconfigured';

        // Waterfall state: ImageData that is scrolled down one row per frame.
        this._waterfallData = null;
        this._waterfallRows = 0;
        this._waterfallCols = 0;

        this._errorDetail = null;

        // Last received spectrum data; held so the canvas can be redrawn on resize.
        this._lastBins    = null;
        this._lastCentreHz = 0;
        this._lastSpanHz   = 0;

        // DX cluster spots overlaid on the spectrum. Each entry is the JSON
        // shape from /api/dxcluster/spots — { callsign, frequencyHz, spotter,
        // comment, receivedUtc }. Only spots within the current span are
        // drawn; clicks within a few pixels of a spot QSY to it precisely.
        this._spots = [];

        // Crosshair state — null when mouse is outside the canvas.
        this._crosshairX  = null;
        this._crosshairY  = null;

        this._resizeObserver = null;
        this._init();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /** Update the spectrum/waterfall with a new frame of FFT data. */
    update({ bins, centreHz, spanHz }) {
        this._lastBins    = bins;
        this._lastCentreHz = centreHz;
        this._lastSpanHz   = spanHz;
        this._render();
    }

    /** Store the latest error detail string for display alongside status overlays. */
    setError(detail) {
        this._errorDetail = detail;
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
     * Respond to SDR lifecycle state changes.
     * @param {string} status  One of: "unconfigured" | "connecting" | "streaming"
     *                                 | "disconnected" | "nodll"
     */
    setStatus(status) {
        this._status = status;

        const container = document.getElementById(this._containerId);
        if (!container) return;

        if (status === 'unconfigured') {
            container.style.display = 'none';
            return;
        }

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

        // Crosshair tracking.
        canvas.addEventListener('mousemove', (e) => {
            const rect = canvas.getBoundingClientRect();
            this._crosshairX = (e.clientX - rect.left) * (canvas.width  / rect.width);
            this._crosshairY = (e.clientY - rect.top)  * (canvas.height / rect.height);
            if (this._lastBins) this._render();
            // Announce cursor frequency to screen readers via a live region (debounced to 1 s).
            if (this._lastSpanHz > 0 && this._vfoHz > 0) {
                clearTimeout(this._announceTimer);
                this._announceTimer = setTimeout(() => {
                    const canvas2 = document.getElementById(this._canvasId);
                    if (!canvas2) return;
                    const W = canvas2.width;
                    const leftHz = this._vfoHz - this._lastSpanHz / 2;
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

    _onCanvasClick(e) {
        if (!this._lastBins || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const canvas = document.getElementById(this._canvasId);
        const rect   = canvas.getBoundingClientRect();
        const x      = e.clientX - rect.left;
        const W      = canvas.width;

        // Only respond to clicks in the spectrum area (top 45%), not the waterfall.
        const specH = Math.floor(canvas.height * 0.45);
        const y     = e.clientY - rect.top;
        if (y > specH * (rect.height / canvas.height)) return;

        // Convert canvas-relative x (CSS pixels) to canvas-internal pixels.
        const canvasX = x * (W / rect.width);
        const leftHz  = this._vfoHz - this._lastSpanHz / 2;

        // If the click is within 8 internal-pixels of a spot's marker, snap
        // to that spot's exact frequency. Otherwise tune to the click x.
        let targetHz = Math.round(leftHz + (canvasX / W) * this._lastSpanHz);
        for (const s of this._spots) {
            const sx = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            if (Math.abs(sx - canvasX) <= 8) {
                targetHz = s.frequencyHz;
                break;
            }
        }

        fetch('/api/cat/frequency/a', {
            method:  'POST',
            headers: { 'Content-Type': 'application/json' },
            body:    JSON.stringify({ frequencyHz: targetHz }),
        }).catch(() => { /* ignore network errors */ });
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
            fetch('/api/cat/frequency/a', {
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

    _render() {
        const canvas = document.getElementById(this._canvasId);
        if (!canvas || !this._lastBins) return;

        const ctx         = canvas.getContext('2d');
        const W           = canvas.width;
        const H           = canvas.height;
        const specH       = Math.floor(H * 0.45);
        const wfH         = H - specH;
        const bins        = this._lastBins;
        const centreHz    = this._lastCentreHz;
        const spanHz      = this._lastSpanHz;

        this._drawSpectrum(ctx, bins, W, specH);
        this._drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz);
        this._drawSpots(ctx, W, specH);
        this._scrollWaterfall(ctx, bins, W, specH, wfH);
        this._drawCrosshair(ctx, W, specH, spanHz);
    }

    // ── DX cluster spot overlay ──────────────────────────────────────────────
    //
    // Draws a small downward-pointing tick at each spot's frequency with the
    // callsign label above. Spots outside the current span are skipped. When
    // multiple spots fall within ~50 px of each other their labels are
    // staggered vertically so they don't overlap.
    _drawSpots(ctx, W, specH) {
        if (!this._spots.length || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

        const leftHz  = this._vfoHz - this._lastSpanHz / 2;
        const rightHz = this._vfoHz + this._lastSpanHz / 2;

        // Build a list of (x, spot) for spots in range, sorted left-to-right.
        const drawList = [];
        for (const s of this._spots) {
            if (s.frequencyHz < leftHz || s.frequencyHz > rightHz) continue;
            const x = ((s.frequencyHz - leftHz) / this._lastSpanHz) * W;
            drawList.push({ x, spot: s });
        }
        if (drawList.length === 0) return;
        drawList.sort((a, b) => a.x - b.x);

        ctx.save();
        ctx.font      = 'bold 10px monospace';
        ctx.textAlign = 'center';
        ctx.fillStyle   = '#ffcc33';
        ctx.strokeStyle = '#ffcc33';
        ctx.lineWidth   = 1.5;

        const minXGap = 50;       // px; labels closer than this are staggered
        const rowHeight = 12;     // px between stagger rows
        const maxRows = 3;        // wrap after 3 rows
        let lastRowX = [-Infinity, -Infinity, -Infinity];

        for (const { x, spot } of drawList) {
            // Pick the lowest row whose last entry is far enough left.
            let row = 0;
            for (let r = 0; r < maxRows; r++) {
                if (x - lastRowX[r] >= minXGap) { row = r; break; }
                if (r === maxRows - 1) row = maxRows - 1; // overflow falls into last row
            }
            lastRowX[row] = x;
            const labelY = 12 + row * rowHeight;

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

    _drawSpectrum(ctx, bins, W, H) {
        const N      = bins.length;
        const dbMin  = -120;
        const dbMax  = 0;
        const range  = dbMax - dbMin;

        // Background
        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, H);

        // Grid lines at every 20 dB
        ctx.strokeStyle = '#1e2030';
        ctx.lineWidth   = 1;
        for (let db = dbMin; db <= dbMax; db += 20) {
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

        // Close path to the bottom to fill
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

        // dB scale labels (right-aligned)
        ctx.fillStyle  = '#667799';
        ctx.font       = '10px monospace';
        ctx.textAlign  = 'right';
        for (let db = dbMin; db <= dbMax; db += 20) {
            const y = H - ((db - dbMin) / range) * H;
            ctx.fillText(`${db} dB`, W - 4, y - 2);
        }

        // Centre frequency label
        if (this._vfoHz > 0) {
            const label = (this._vfoHz / 1e6).toFixed(6) + ' MHz';
            ctx.font      = '12px monospace';
            ctx.fillStyle = '#44aaff';
            ctx.textAlign = 'center';
            ctx.fillText(label, W / 2, 14);
        }
    }

    // ── Frequency axis ───────────────────────────────────────────────────────

    _drawFrequencyAxis(ctx, bins, W, specH, centreHz, spanHz) {
        const axisH  = 20;
        const tickY0 = specH - axisH;       // top of axis strip
        const labelY = specH - 4;           // baseline for text

        ctx.fillStyle = '#111118';
        ctx.fillRect(0, tickY0, W, axisH);

        // VFO centre marker line (drawn first, behind labels)
        ctx.strokeStyle = 'rgba(0, 170, 255, 0.4)';
        ctx.lineWidth   = 1;
        ctx.beginPath();
        ctx.moveTo(W / 2, 0);
        ctx.lineTo(W / 2, tickY0);
        ctx.stroke();

        // Only skip labels when FrequencyA has never been set (C# long default = 0).
        // Any non-zero persisted frequency is treated as valid; FTdx101MP range is 30 kHz–75 MHz.
        if (this._vfoHz <= 0) {
            ctx.fillStyle = '#667799';
            ctx.font = '10px monospace';
            ctx.textAlign = 'center';
            ctx.fillText('No VFO frequency available', W / 2, labelY);
            return;
        }

        // Choose a "nice" tick interval that gives roughly 6–12 ticks across the span.
        // Candidate steps in Hz: 50k, 100k, 200k, 250k, 500k, 1M, 2M, 5M, 10M
        const steps = [50e3, 100e3, 200e3, 250e3, 500e3, 1e6, 2e6, 5e6, 10e6];
        const targetTicks = 8;
        const stepHz = steps.find(s => spanHz / s <= targetTicks) ?? steps[steps.length - 1];

        // First tick at the next multiple of stepHz above the left edge
        const leftHz  = this._vfoHz - spanHz / 2;
        const firstHz = Math.ceil(leftHz / stepHz) * stepHz;

        ctx.font      = '10px monospace';
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
        if (this._crosshairX === null || this._lastSpanHz <= 0 || this._vfoHz <= 0) return;

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
        const leftHz  = this._vfoHz - spanHz / 2;
        const freqHz  = leftHz + (x / W) * spanHz;
        const label   = (freqHz / 1e6).toFixed(6) + ' MHz';

        ctx.font      = '11px monospace';
        const pad     = 4;
        const tw      = ctx.measureText(label).width;

        // Position label to the right of cursor, flip left near the right edge.
        const lx = (x + tw + pad * 2 + 6 < W) ? x + pad : x - tw - pad * 2;
        const ly = Math.max(14, Math.min(y - pad, specTop - 4));

        ctx.fillStyle = 'rgba(0, 0, 0, 0.6)';
        ctx.fillRect(lx, ly - 12, tw + pad * 2, 16);

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
            const [r, g, b] = SpectrumPanel._dbToColor(bins[binIdx]);
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

        const messages = {
            connecting:   'Connecting to SDR device…',
            disconnected: 'SDR device unavailable — retrying every 5 s',
            nodll:        'SoapySDR.dll not found — install SoapySDR + device driver',
        };

        const line1 = messages[status] ?? `SDR status: ${status}`;
        const line2 = status === 'disconnected' && this._errorDetail
            ? this._errorDetail
            : null;

        ctx.fillStyle = '#0a0a14';
        ctx.fillRect(0, 0, W, H);

        ctx.fillStyle = '#8899bb';
        ctx.textAlign = 'center';

        ctx.font = '14px sans-serif';
        ctx.fillText(line1, W / 2, H / 2 - (line2 ? 10 : 0));

        if (line2) {
            ctx.font      = '11px sans-serif';
            ctx.fillStyle = '#cc6655';
            ctx.fillText(line2, W / 2, H / 2 + 12);
        }
    }

    // ── Accessibility ────────────────────────────────────────────────────────

    /** Announce text to screen readers via the shared ARIA live region in site.js. */
    _announceToScreenReader(text) {
        let lr = document.getElementById('_sr_live');
        if (!lr) {
            // Fallback: create a local live region if site.js hasn't run yet.
            lr = document.createElement('div');
            lr.id = '_sr_live';
            lr.setAttribute('aria-live', 'polite');
            lr.setAttribute('aria-atomic', 'true');
            lr.style.cssText = 'position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden;';
            document.body.appendChild(lr);
        }
        lr.textContent = '';
        requestAnimationFrame(() => { lr.textContent = text; });
    }

    // ── Color mapping ────────────────────────────────────────────────────────

    /**
     * Maps a dBFS value (−120 … 0) to an RGB thermal colour.
     * Black → blue → cyan → green → yellow → red.
     */
    static _dbToColor(db) {
        const t = Math.max(0, Math.min(1, (db + 120) / 120));
        if (t < 0.2)  return [0,                   0,                   Math.round(t * 5 * 180)];
        if (t < 0.4)  return [0,                   Math.round((t - 0.2) * 5 * 200), 180];
        if (t < 0.6)  return [0,                   200,                 Math.round(180 - (t - 0.4) * 5 * 180)];
        if (t < 0.8)  return [Math.round((t - 0.6) * 5 * 255), 200,    0];
        return               [255,                 Math.round(200 - (t - 0.8) * 5 * 200), 0];
    }
}
