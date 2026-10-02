// Twin PBT panel: the IC-7300's Digital Passband Tuning, two receiver-wide
// shift values - PBT1 (inner) = CI-V 14 07, PBT2 (outer) = CI-V 14 08 -
// backed by /api/cat/pbt. Each is 0-255 with 128 as centre (no shift). The
// radio holds the state; the panel is a thin read/write proxy, so the main
// page's dialog and the /AudioFilter pop-out window can both show it.
//
// IWC-local rather than core: the 0-255 / 128-centre scale is Icom's.
//
// One panel per VFO button (either panel can open it); on the single-
// receiver IC-7300 both drive the same passband. Element ids keep the
// audioFilter<Part><VFO> / pbt<Part><VFO> names from
// Pages/Shared/_AudioFilterPartial, so saved drag positions still apply.

export const PBT_CENTRE = 128;

/** 128 -> "Centre"; otherwise a signed offset from centre. */
export function fmtPbt(v) {
    if (v === PBT_CENTRE) return 'Centre';
    const off = v - PBT_CENTRE;
    return (off > 0 ? '+' : '') + off;
}

export class AudioFilterPanel {
    /**
     * @param {'A'|'B'} vfo
     * @param {object} [opts]
     * @param {HTMLElement} [opts.anchor] the main page's Twin PBT button;
     *        on first open, with no saved position, the dialog appears under it
     */
    constructor(vfo, { anchor = null } = {}) {
        this._vfo    = vfo === 'B' ? 'B' : 'A';
        this._anchor = anchor;
        this._dialogId = 'audioFilterDialog' + this._vfo;
        this._busy = false;
        // A slider the operator is dragging, which a re-read must not move.
        this._dragging = null;
        this._lastRead = 0;
    }

    get vfo() { return this._vfo; }

    get dialog() { return document.getElementById(this._dialogId); }

    _el(id) { return document.getElementById(id + this._vfo); }

    init() {
        const inner = this._el('pbtInnerSlider'), outer = this._el('pbtOuterSlider');
        const wire = (slider, label, edge) => {
            if (!slider) return;
            slider.addEventListener('input',  e => this._el(label).textContent = fmtPbt(parseInt(e.target.value, 10)));
            slider.addEventListener('change', e => this._write(edge, parseInt(e.target.value, 10)));
            slider.addEventListener('pointerdown', e => { this._dragging = e.target; });
        };
        wire(inner, 'pbtInnerLabel', 'inner');
        wire(outer, 'pbtOuterLabel', 'outer');
        window.addEventListener('pointerup',     () => { this._dragging = null; });
        window.addEventListener('pointercancel', () => { this._dragging = null; });
        this._el('pbtClearBtn')?.addEventListener('click', async () => {
            this._set(inner, 'pbtInnerLabel', PBT_CENTRE);
            this._set(outer, 'pbtOuterLabel', PBT_CENTRE);
            await this._write('inner', PBT_CENTRE);
            await this._write('outer', PBT_CENTRE);
        });

        // Nothing tells an open panel that the PBT knobs on the radio's front
        // panel have moved. Re-read when the operator comes back to it: the
        // window gaining focus (a pop-out on another monitor, or the main
        // page after another app), or a click on the dialog itself.
        window.addEventListener('focus', () => this._refreshIfShowing());
        document.addEventListener('visibilitychange', () => {
            if (document.visibilityState === 'visible') this._refreshIfShowing();
        });
        this.dialog?.addEventListener('pointerdown', () => this._refreshIfShowing());
        return this;
    }

    _refreshIfShowing() {
        if (!this.dialog?.open) return;
        // A click that also focuses the window fires both events; one read
        // is enough.
        if (Date.now() - this._lastRead < 1000) return;
        this.refresh();
    }

    /** Show the dialog (main page) and read the radio. */
    async open() {
        const d = this.dialog;
        if (d && typeof d.show === 'function' && !d.open) {
            let saved = false;
            try { saved = !!localStorage.getItem('dlgPos_' + this._dialogId); } catch { /* ignore */ }
            if (!saved && this._anchor) {
                const r = this._anchor.getBoundingClientRect();
                d.style.left = Math.round(r.left) + 'px';
                d.style.top  = Math.round(r.bottom + 6) + 'px';
                d.style.transform = 'none';
            }
            d.show();
        }
        await this.refresh();
    }

    /** A VFO's mode changed: re-read, if it is this panel's and it is showing. */
    onModeChanged(changedVfo) {
        if (changedVfo !== this._vfo) return;
        if (!this.dialog?.open) return;
        this.refresh();
    }

    _set(slider, label, value) {
        if (!slider) return;
        // Mid-drag: the slider and its label belong to the operator.
        if (this._dragging === slider) return;
        slider.value = value;
        const l = this._el(label);
        if (l) l.textContent = fmtPbt(value);
    }

    async refresh() {
        if (this._busy) return;
        this._busy = true;
        this._lastRead = Date.now();
        const status = this._el('audioFilterStatus');
        try {
            const resp = await fetch('/api/cat/pbt');
            if (!resp.ok) {
                if (status) status.textContent = 'Failed to read Twin PBT.';
                return;
            }
            const data = await resp.json();
            if (status) status.textContent = '';
            this._set(this._el('pbtInnerSlider'), 'pbtInnerLabel',
                typeof data.inner === 'number' ? data.inner : PBT_CENTRE);
            this._set(this._el('pbtOuterSlider'), 'pbtOuterLabel',
                typeof data.outer === 'number' ? data.outer : PBT_CENTRE);
        } catch (e) {
            console.error(`twinPbt[${this._vfo}].refresh error:`, e);
        } finally {
            this._busy = false;
        }
    }

    async _write(edge, value) {
        const status = this._el('audioFilterStatus');
        try {
            const resp = await fetch('/api/cat/pbt/' + edge, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ value }),
            });
            if (!resp.ok && status) status.textContent = `Failed to set PBT ${edge}.`;
        } catch (e) { console.error(`twinPbt[${this._vfo}].write error:`, e); }
    }
}
