/**
 * Experimental UI (/flexui): Fabio Valente's Buttons panel (YWC
 * _Buttons.cshtml) on IWC's own toolbar buttons.
 *
 * Classic's code rewrites #atuBtn's and #atuTuneBtn's whole text and swaps
 * Bootstrap classes on them and on #voxBtn, so they cannot simply be
 * restyled as keys - the LED and label spans would be wiped on the first
 * update. Instead each key here stands in for a Classic button parked,
 * hidden, inside the panel: pressing the key presses the Classic button, and
 * the key's LED and label follow the Classic button's classes and text.
 * ATU's tap / press-and-hold is Classic's own pointer handling, so the key
 * hands its pointer events straight on to #atuBtn.
 */

const $ = (id) => document.getElementById(id);

/** The Classic buttons that have no id, found by what they open. */
function byOnclick(text) {
    return [...document.querySelectorAll("button[onclick]")].find((b) => b.getAttribute('onclick').includes(text)) || null;
}

function key(label, classic, { led = false } = {}) {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = led ? 'btn toggle-dd__btn' : 'btn toggle-dd__btn toggle-dd__btn--action';
    btn.id = classic.id ? classic.id + 'Key' : '';
    for (const attr of ['aria-label', 'title']) {
        const v = classic.getAttribute(attr);
        if (v) btn.setAttribute(attr, v);
    }
    if (led) {
        const dot = document.createElement('span');
        dot.className = 'toggle-dd__led';
        dot.setAttribute('aria-hidden', 'true');
        btn.appendChild(dot);
    }
    const text = document.createElement('span');
    text.className = 'toggle-dd__label';
    text.textContent = label;
    btn.appendChild(text);
    return btn;
}

function follow(classic, fn) {
    new MutationObserver(fn).observe(classic, { childList: true, characterData: true, subtree: true, attributes: true, attributeFilter: ['class', 'aria-label', 'title'] });
    fn();
}

/** Build the Buttons panel section, parking the Classic buttons inside it. */
export function buildButtonsKeys() {
    const section = document.createElement('div');
    section.id = 'buttonsSection';
    section.className = 'op-col op-col--buttons';
    const classic = document.createElement('div');
    classic.className = 'iwc-flex-buttons-classic';
    classic.hidden = true;

    const c = {
        atu: $('atuBtn'),
        tune: $('atuTuneBtn'),
        fm: byOnclick("'fmDialog').show("),
        vox: $('voxBtn'),
        cw: byOnclick('openCwDialog('),
        cwRead: $('cwReaderOpenBtn'),
        cwSend: $('cwSendOpenBtn'),
        rtty: $('rttyTunerOpenBtn'),
        dxSpots: $('dxSpotsOpenBtn'),
        dxWatch: byOnclick("'dxWatchDialog').show("),
        voice: $('voiceBtn'),
    };

    // Same order as YWC's panel.
    const add = (name, label, opts) => {
        const b = c[name];
        if (!b) return null;
        const k = key(label, b, opts);
        section.appendChild(k);
        classic.appendChild(b);
        return k;
    };

    const atu = add('atu', 'ATU', { led: true });
    if (atu) {
        const real = c.atu;
        const pass = (type) => atu.addEventListener(type, (e) => {
            real.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerId: e.pointerId, pointerType: e.pointerType }));
        });
        for (const type of ['pointerdown', 'pointerup', 'pointerleave', 'pointercancel']) pass(type);
        // Enter / Space give a click with no pointer events: a tap.
        atu.addEventListener('click', (e) => {
            if (e.detail !== 0) return;
            real.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
            real.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
        });
        atu.style.touchAction = 'manipulation';
        atu.style.userSelect = 'none';
        follow(real, () => {
            const tuning = real.dataset.tuning === 'true';
            atu.querySelector('.toggle-dd__led').classList.toggle('is-on', real.classList.contains('btn-success'));
            atu.classList.toggle('is-tuning', tuning);
            atu.querySelector('.toggle-dd__label').textContent = tuning ? 'Tuning…' : 'ATU';
            atu.setAttribute('aria-pressed', real.classList.contains('btn-success') ? 'true' : 'false');
        });
    }

    const press = (k, name) => k?.addEventListener('click', () => c[name].click());

    const tune = add('tune', 'Tune');
    press(tune, 'tune');
    if (tune) {
        follow(c.tune, () => {
            tune.querySelector('.toggle-dd__label').textContent = c.tune.textContent.trim() || 'Tune';
            tune.classList.toggle('is-tuning', c.tune.classList.contains('btn-danger'));
            for (const attr of ['aria-label', 'title']) tune.setAttribute(attr, c.tune.getAttribute(attr) || '');
        });
    }

    press(add('fm', 'FM Rep'), 'fm');

    const vox = add('vox', 'VOX', { led: true });
    press(vox, 'vox');
    if (vox) follow(c.vox, () => vox.querySelector('.toggle-dd__led').classList.toggle('is-on', c.vox.classList.contains('btn-warning')));

    press(add('cw', 'CW'), 'cw');
    press(add('cwRead', 'CW Read'), 'cwRead');
    press(add('cwSend', 'CW Send'), 'cwSend');
    press(add('rtty', 'RTTY'), 'rtty');
    press(add('dxSpots', 'DX Spots'), 'dxSpots');
    press(add('dxWatch', 'DX Watch'), 'dxWatch');
    press(add('voice', 'Voice'), 'voice');

    section.appendChild(classic);
    return section;
}
